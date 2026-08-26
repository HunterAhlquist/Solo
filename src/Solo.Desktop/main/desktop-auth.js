// Google refuses to run its sign-in pages inside Electron, so the desktop
// shell opens the instance's own sign-in page in the system browser. The
// browser returns a one-time authorization code to an ephemeral IPv4 loopback
// listener; PKCE makes that code useless without the verifier held here.
//
//   1. Listen on 127.0.0.1:{ephemeral port}; generate a PKCE verifier.
//   2. POST {instance}/api/auth/desktop/start with the port and challenge.
//   3. Open {instance}/desktop-auth?state=… in the default browser.
//   4. Receive the authorization code on loopback and redeem it with the
//      verifier for a ready-made solo_ session.
const crypto = require('crypto');
const http = require('http');
const { net, shell } = require('electron');

const LOOPBACK_HOST = '127.0.0.1';
const LOOPBACK_PATH = '/desktop-auth/callback';

class DesktopAuth {
    constructor(dependencies = {}) {
        this._createServer = dependencies.createServer ?? (handler => http.createServer(handler));
        this._fetch = dependencies.fetch ?? ((...args) => net.fetch(...args));
        this._openExternal = dependencies.openExternal ?? (url => shell.openExternal(url));
        this._randomBytes = dependencies.randomBytes ?? (size => crypto.randomBytes(size));
        this._pending = null;
    }

    /**
     * Starts a browser sign-in against the instance. Resolves once the browser
     * is open; the session arrives later through onSession.
     */
    async begin({ instanceUrl, onSession, onFailure }) {
        this._closePending(this._pending);
        if (!instanceUrl) {
            onFailure('No Solo instance configured.');
            return;
        }

        const verifier = base64Url(this._randomBytes(32));
        const pending = {
            server: null,
            flow: null,
            verifier,
            processing: false,
            timer: null,
            onSession,
            onFailure,
        };
        this._pending = pending;
        pending.server = this._createServer((request, response) => {
            void this._handleCallback(request, response, pending).catch(() => {
                if (!response.headersSent) {
                    sendPage(response, 500, 'Sign-in failed', 'Start sign-in again from Solo Desktop.');
                } else if (!response.writableEnded) {
                    response.end();
                }
                this._failPending(pending, 'The browser sign-in could not be completed. Try again.');
            });
        });

        try {
            await listen(pending.server);
        } catch (error) {
            this._failPending(pending, `Could not open the local sign-in callback: ${error.message}`);
            return;
        }
        if (this._pending !== pending) {
            this._closeServer(pending.server);
            return;
        }

        const address = pending.server.address();
        if (!address || typeof address === 'string') {
            this._failPending(pending, 'Could not determine the local sign-in callback.');
            return;
        }

        let response;
        try {
            response = await this._postJson(`${instanceUrl}/api/auth/desktop/start`, {
                description: `Solo Desktop (${platformLabel()})`,
                codeChallenge: pkceChallenge(verifier),
                loopbackPort: address.port,
            });
        } catch (error) {
            this._failPending(pending, `Could not reach the server: ${error.message}`);
            return;
        }
        if (this._pending !== pending) {
            return;
        }

        if (response.status === 404 || response.status === 405) {
            this._failPending(pending,
                'This Solo server does not support secure desktop sign-in — update the server and try again.');
            return;
        }
        if (!response.ok) {
            this._failPending(pending,
                `The server refused to start the sign-in (HTTP ${response.status}).`);
            return;
        }

        const flow = await response.json().catch(() => null);
        if (typeof flow?.state !== 'string' || !Number.isFinite(Date.parse(flow?.expiresAt)) ||
            typeof flow?.deviceCode === 'string') {
            this._failPending(pending,
                'This Solo server does not support secure desktop sign-in — update the server and try again.');
            return;
        }
        pending.flow = { ...flow, instanceUrl };

        const remaining = Math.max(1, Date.parse(flow.expiresAt) - Date.now());
        pending.timer = setTimeout(() => {
            this._failPending(pending, 'The sign-in timed out. Try again.');
        }, remaining);

        try {
            await this._openExternal(
                `${instanceUrl}/desktop-auth?state=${encodeURIComponent(flow.state)}`);
        } catch (error) {
            this._failPending(pending, `Could not open the system browser: ${error.message}`);
        }
    }

    async _handleCallback(request, response, pending) {
        if (this._pending !== pending || !pending.flow) {
            sendPage(response, 410, 'Sign-in expired', 'Start sign-in again from Solo Desktop.');
            return;
        }

        const address = pending.server.address();
        const expectedHost = address && typeof address !== 'string'
            ? `${LOOPBACK_HOST}:${address.port}`
            : null;
        if (request.method !== 'GET' || request.headers.host !== expectedHost) {
            sendPage(response, 404, 'Not found', 'This callback is only available to Solo Desktop.');
            return;
        }

        let callback;
        try {
            callback = new URL(request.url, `http://${expectedHost}`);
        } catch {
            sendPage(response, 400, 'Invalid callback', 'Start sign-in again from Solo Desktop.');
            return;
        }

        const code = callback.searchParams.get('code');
        const state = callback.searchParams.get('state');
        if (callback.pathname !== LOOPBACK_PATH || state !== pending.flow.state || !code) {
            sendPage(response, 404, 'Invalid callback', 'Start sign-in again from Solo Desktop.');
            return;
        }
        if (pending.processing) {
            sendPage(response, 409, 'Sign-in is finishing', 'Return to Solo Desktop.');
            return;
        }
        pending.processing = true;

        let redeemed;
        try {
            redeemed = await this._postJson(`${pending.flow.instanceUrl}/api/auth/desktop/redeem`, {
                state,
                authorizationCode: code,
                codeVerifier: pending.verifier,
            });
        } catch {
            pending.processing = false;
            sendPage(response, 503, 'Could not reach Solo', 'Reload this page to finish sign-in.');
            return;
        }

        if (!redeemed.ok) {
            sendPage(response, 400, 'Sign-in could not be completed',
                'The authorization expired or was already used. Start again from Solo Desktop.');
            this._failPending(pending, 'The browser sign-in could not be redeemed. Try again.');
            return;
        }

        const session = await redeemed.json().catch(() => null);
        if (typeof session?.token !== 'string') {
            sendPage(response, 502, 'Invalid response', 'Update Solo Desktop and try again.');
            this._failPending(pending,
                'The server answered, but not with a session — update the server and try again.');
            return;
        }

        sendPage(response, 200, 'You’re signed in', 'Return to Solo Desktop. This tab can be closed.');
        this._closePending(pending);
        try {
            await Promise.resolve(pending.onSession(session));
        } catch {
            pending.onFailure('The session was created, but Solo Desktop could not store it. Try again.');
        }
    }

    _postJson(url, body) {
        return this._fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body),
            cache: 'no-store',
        });
    }

    _failPending(pending, message) {
        if (this._pending !== pending) {
            return;
        }
        this._closePending(pending);
        pending.onFailure(message);
    }

    _closePending(pending) {
        if (!pending) {
            return;
        }
        if (this._pending === pending) {
            this._pending = null;
        }
        clearTimeout(pending.timer);
        this._closeServer(pending.server);
    }

    _closeServer(server) {
        if (server?.listening) {
            server.close();
        }
    }
}

function listen(server) {
    return new Promise((resolve, reject) => {
        const onError = error => reject(error);
        server.once('error', onError);
        server.listen(0, LOOPBACK_HOST, () => {
            server.removeListener('error', onError);
            resolve();
        });
    });
}

function sendPage(response, status, title, message) {
    response.writeHead(status, {
        'Content-Type': 'text/html; charset=utf-8',
        'Cache-Control': 'no-store',
        'Content-Security-Policy': "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'",
        'Referrer-Policy': 'no-referrer',
        'X-Content-Type-Options': 'nosniff',
    });
    response.end(`<!doctype html><meta charset="utf-8"><title>${title}</title>` +
        '<style>body{font:16px system-ui;margin:4rem;max-width:36rem}h1{font-size:1.5rem}</style>' +
        `<h1>${title}</h1><p>${message}</p>`);
}

function pkceChallenge(verifier) {
    return base64Url(crypto.createHash('sha256').update(verifier, 'ascii').digest());
}

function base64Url(value) {
    return Buffer.from(value).toString('base64url');
}

function platformLabel() {
    switch (process.platform) {
        case 'darwin': return 'macOS';
        case 'win32': return 'Windows';
        case 'linux': return 'Linux';
        default: return process.platform;
    }
}

module.exports = { DesktopAuth, pkceChallenge };
