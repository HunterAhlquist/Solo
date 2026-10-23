import assert from 'node:assert/strict';
import http from 'node:http';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { DesktopAuth, pkceChallenge } = require('../main/desktop-auth');

const authorizationCode = Buffer.alloc(32, 7).toString('base64url');
const state = '0123456789abcdef0123456789abcdef';
let startRequest = null;
let redeemRequest = null;

const instance = http.createServer(async (request, response) => {
    if (request.method === 'POST' && request.url === '/api/auth/desktop/start') {
        startRequest = await readJson(request);
        sendJson(response, 200, {
            state,
            expiresAt: new Date(Date.now() + 30_000).toISOString(),
        });
        return;
    }

    if (request.method === 'POST' && request.url === '/api/auth/desktop/redeem') {
        redeemRequest = await readJson(request);
        const proofMatches = redeemRequest.state === state &&
            redeemRequest.authorizationCode === authorizationCode &&
            pkceChallenge(redeemRequest.codeVerifier) === startRequest.codeChallenge;
        sendJson(response, proofMatches ? 200 : 404, proofMatches
            ? { token: 'solo_test-only', user: { email: 'test@example.com' } }
            : { error: 'invalid proof' });
        return;
    }

    response.writeHead(404).end();
});
await listen(instance);

let openedUrl = null;
let session = null;
const failures = [];
const desktop = new DesktopAuth({
    fetch: globalThis.fetch,
    openExternal: async url => {
        openedUrl = new URL(url);
        assert.equal(openedUrl.origin, instanceUrl);
        assert.equal(openedUrl.pathname, '/desktop-auth');
        assert.equal(openedUrl.searchParams.get('state'), state);

        const callbackBase = `http://127.0.0.1:${startRequest.loopbackPort}/desktop-auth/callback`;
        const unrelated = await fetch(`${callbackBase}?code=${authorizationCode}&state=wrong`);
        assert.equal(unrelated.status, 404, 'The listener accepted a callback for another flow.');

        const callback = await fetch(`${callbackBase}?code=${authorizationCode}&state=${state}`);
        assert.equal(callback.status, 200);
        assert.match(await callback.text(), /Return to Solo Desktop/);
    },
});

const instanceUrl = `http://127.0.0.1:${instance.address().port}`;
try {
    await desktop.begin({
        instanceUrl,
        onSession: value => { session = value; },
        onFailure: message => failures.push(message),
    });

    assert.ok(startRequest, 'The desktop process did not open a server flow.');
    assert.equal(startRequest.loopbackPort > 1023, true);
    assert.equal(startRequest.codeChallenge.length, 43);
    assert.equal('codeVerifier' in startRequest, false, 'The PKCE verifier left the desktop process.');
    assert.ok(redeemRequest, 'The loopback callback was not redeemed.');
    assert.equal(session?.token, 'solo_test-only');
    assert.deepEqual(failures, []);
    console.log('Desktop sign-in returned a PKCE-bound loopback code to the initiating process.');

    // In the macOS sheet: the same flow, but the browser is never opened, the
    // callback redirects to the sheet's completion URL (which closes it), and
    // the sheet is let go once the flow ends.
    const completionUrl = 'com.solo.desktop://desktop-auth/done';
    let sheetUrl = null;
    let sheetCancelled = 0;
    let callbackLocation = null;
    session = null;
    redeemRequest = null;
    const inApp = new DesktopAuth({
        fetch: globalThis.fetch,
        openExternal: () => assert.fail('The sheet flow opened the default browser.'),
    });
    await inApp.begin({
        instanceUrl,
        onSession: value => { session = value; },
        onFailure: message => failures.push(message),
        sheet: url => {
            sheetUrl = new URL(url);
            return { completionUrl, cancel: () => { sheetCancelled++; } };
        },
    });
    assert.equal(sheetUrl?.pathname, '/desktop-auth');
    const callback = await fetch(
        `http://127.0.0.1:${startRequest.loopbackPort}/desktop-auth/callback?code=${authorizationCode}&state=${state}`,
        { redirect: 'manual' });
    callbackLocation = callback.headers.get('location');
    assert.equal(callback.status, 302);
    assert.equal(callbackLocation, completionUrl);
    assert.equal(session?.token, 'solo_test-only');
    assert.equal(sheetCancelled, 1, 'The sheet was not let go after sign-in.');
    assert.deepEqual(failures, []);

    // Dismissing the sheet ends the flow with a message, not a silent hang.
    let dismiss = null;
    await inApp.begin({
        instanceUrl,
        onSession: () => assert.fail('A dismissed sheet produced a session.'),
        onFailure: message => failures.push(message),
        sheet: (url, onDismissed) => {
            dismiss = onDismissed;
            return { completionUrl, cancel: () => {} };
        },
    });
    dismiss('cancelled');
    assert.deepEqual(failures, ['Sign-in was cancelled.']);
    console.log('In the sign-in sheet, the callback closes the sheet and Cancel ends the flow.');
} finally {
    await close(instance);
}

function readJson(request) {
    return new Promise((resolve, reject) => {
        let body = '';
        request.setEncoding('utf8');
        request.on('data', chunk => { body += chunk; });
        request.on('end', () => {
            try {
                resolve(JSON.parse(body));
            } catch (error) {
                reject(error);
            }
        });
        request.on('error', reject);
    });
}

function sendJson(response, status, value) {
    response.writeHead(status, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify(value));
}

function listen(server) {
    return new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => {
            server.removeListener('error', reject);
            resolve();
        });
    });
}

function close(server) {
    return new Promise((resolve, reject) => {
        server.close(error => error ? reject(error) : resolve());
    });
}


for (const platform of ['darwin', 'win32']) {
    const denied = [];
    const client = new DesktopAuth({
        platform,
        appName: 'Solo (Preview)',
        fetch: async () => { throw new Error('net::ERR_ADDRESS_UNREACHABLE'); },
        openExternal: () => assert.fail('A failed connection must not open the sign-in page.'),
    });
    await client.begin({
        instanceUrl: 'https://solo-preview.example.test',
        onSession: () => assert.fail('No session should be created.'),
        onFailure: message => denied.push(message),
    });
    assert.equal(denied.length, 1);
    assert.match(denied[0], /ERR_ADDRESS_UNREACHABLE/);
    if (platform === 'darwin') {
        assert.match(denied[0], /Solo \(Preview\).*Local Network/);
    } else {
        assert.doesNotMatch(denied[0], /System Settings/);
    }
}
console.log('Unreachable macOS sign-in explains Local Network access without treating other platforms as macOS.');
