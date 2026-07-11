// Google refuses to run its sign-in pages inside Electron ("this browser or
// app may not be secure"), no matter how Chrome-like the user agent looks. So
// the desktop shell signs in the way Google prescribes for installed apps: in
// the user's real browser — on the instance's own origin, which the operator
// already registered with Google for the web client. The flow is device-code
// shaped (see the API's DesktopAuthController):
//
//   1. POST {instance}/api/auth/desktop/start  → { state, deviceCode, expiresAt }
//   2. open {instance}/desktop-auth?state=…    in the default browser
//   3. POST {instance}/api/auth/desktop/poll   with the deviceCode until the
//      browser sign-in completes; the answer is a ready-made solo_ session.
//
// The state only identifies the flow in the browser URL; the deviceCode never
// leaves this process, and the instance hands out the session exactly once —
// a snooped URL cannot redeem anything.
const { net, shell } = require('electron');

const POLL_INTERVAL_MS = 2000;

class DesktopAuth {
    constructor() {
        // Bumped on every begin(): an older flow's poll loop sees a newer
        // generation and stops silently, so only one flow is ever live.
        this._generation = 0;
    }

    /**
     * Starts a browser sign-in against the instance. Resolves once the
     * browser is open (or the flow failed to start); the session itself
     * arrives later through onSession, a failure through onFailure.
     */
    async begin({ instanceUrl, onSession, onFailure }) {
        const generation = ++this._generation;
        if (!instanceUrl) {
            onFailure('No Solo instance configured.');
            return;
        }

        let flow;
        try {
            const response = await postJson(`${instanceUrl}/api/auth/desktop/start`, {
                description: `Solo Desktop (${platformLabel()})`,
            });
            if (response.status === 404 || response.status === 405) {
                onFailure('This Solo server does not support desktop sign-in yet — update the server and try again.');
                return;
            }

            if (!response.ok) {
                onFailure(`The server refused to start the sign-in (HTTP ${response.status}).`);
                return;
            }

            flow = await response.json();
        } catch (err) {
            onFailure(`Could not reach the server: ${err.message}`);
            return;
        }

        if (typeof flow?.state !== 'string' || typeof flow?.deviceCode !== 'string') {
            onFailure('The server answered, but not with a sign-in flow — update the server and try again.');
            return;
        }

        shell.openExternal(`${instanceUrl}/desktop-auth?state=${encodeURIComponent(flow.state)}`);
        this._poll({ instanceUrl, flow, generation, onSession, onFailure });
    }

    async _poll({ instanceUrl, flow, generation, onSession, onFailure }) {
        const deadline = Date.parse(flow.expiresAt) || Date.now() + 10 * 60 * 1000;

        while (this._generation === generation && Date.now() < deadline) {
            await sleep(POLL_INTERVAL_MS);
            if (this._generation !== generation) {
                return;
            }

            let response;
            try {
                response = await postJson(`${instanceUrl}/api/auth/desktop/poll`, {
                    deviceCode: flow.deviceCode,
                });
            } catch {
                continue; // Network blip; the flow may still complete.
            }

            if (response.status === 202) {
                continue; // Still waiting on the browser.
            }

            if (!response.ok) {
                onFailure('The sign-in expired before it completed. Try again.');
                return;
            }

            const session = await response.json().catch(() => null);
            if (typeof session?.token !== 'string') {
                onFailure('The server answered, but not with a session — update the server and try again.');
                return;
            }

            onSession(session);
            return;
        }

        if (this._generation === generation) {
            onFailure('The sign-in timed out. Try again.');
        }
    }
}

function postJson(url, body) {
    return net.fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
        cache: 'no-store',
    });
}

function platformLabel() {
    switch (process.platform) {
        case 'darwin': return 'macOS';
        case 'win32': return 'Windows';
        case 'linux': return 'Linux';
        default: return process.platform;
    }
}

function sleep(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

module.exports = { DesktopAuth };
