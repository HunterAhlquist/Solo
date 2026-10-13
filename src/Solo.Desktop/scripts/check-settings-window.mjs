import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

if (process.platform !== 'darwin') {
    console.log('SKIP: native settings-window effects are macOS-only.');
    process.exit(0);
}

const desktopDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const electron = process.env.SOLO_GLASS_APP ?? path.join(desktopDir, 'node_modules', '.bin', 'electron');
const userDataDir = mkdtempSync(path.join(os.tmpdir(), 'solo-settings-check-'));
// Stands in for a Solo instance: enough of one to pass the connection test.
// Still in setup, so adding it opens its window without a browser sign-in.
const fixture = createServer((request, response) => {
    response.setHeader('Content-Type', 'application/json');
    if (request.url === '/api/config/auth') {
        response.end('{"authority":"","clientId":"","provider":"generic","setupCompleted":false}');
        return;
    }

    response.statusCode = 401;
    response.end('{"error":"unauthorized fixture request"}');
});
const fixtureUrl = `http://127.0.0.1:${await listen(fixture)}`;
const env = { ...process.env, SOLO_USER_DATA_DIR: userDataDir };
delete env.SOLO_DISABLE_LIQUID_GLASS;
delete env.SOLO_GLASS_APP;
const child = spawn(electron, ['--remote-debugging-port=0', '.'], {
    cwd: desktopDir,
    env,
    stdio: ['ignore', 'pipe', 'pipe'],
});

let output = '';
child.stdout.on('data', chunk => { output += chunk.toString(); });
child.stderr.on('data', chunk => { output += chunk.toString(); });
child.on('error', error => { output += `Electron launch failed: ${error.message}\n`; });

try {
    const browserSocket = await waitFor(() =>
        output.match(/DevTools listening on (ws:\/\/[^\s]+)/)?.[1],
    5000, 'Electron did not expose its test debugger.');
    const browserOrigin = browserSocket.replace(/^ws:/, 'http:').replace(/\/devtools\/browser\/.*$/, '');
    const settingsTarget = await targetMatching(browserOrigin,
        target => target.type === 'page' && target.url.endsWith('/settings/settings.html'));
    await new Promise(resolve => setTimeout(resolve, 250));

    const appearance = await evaluate(settingsTarget.webSocketDebuggerUrl, `({
        hasBridge: typeof window.soloSettings?.add === 'function',
        firstRun: document.getElementById('instances').children.length === 0 &&
            document.getElementById('close').textContent === 'Quit',
        glass: document.documentElement.classList.contains('solo-settings--glass'),
        controlHidden: document.getElementById('native-effects-setting').hidden,
        controlChecked: document.getElementById('native-effects').checked,
        dragRegion: getComputedStyle(document.body).getPropertyValue('-webkit-app-region').trim(),
        inputRegion: getComputedStyle(document.getElementById('url'))
            .getPropertyValue('-webkit-app-region').trim(),
        glassWash: getComputedStyle(document.documentElement)
            .getPropertyValue('--glass-wash').trim(),
        backdrop: getComputedStyle(document.body, '::before').backdropFilter ||
            getComputedStyle(document.body, '::before').webkitBackdropFilter
    })`);
    console.log(JSON.stringify({ settingsAppearance: appearance }, null, 2));
    if (!appearance.hasBridge || !appearance.firstRun || !appearance.glass || appearance.controlHidden ||
        !appearance.controlChecked || appearance.backdrop === 'none' ||
        appearance.dragRegion !== 'drag' || appearance.inputRegion !== 'no-drag' ||
        !appearance.glassWash.includes('/ 48%')) {
        throw new Error('The settings window did not start with its native appearance, drag regions, and enabled control.');
    }

    const refused = await evaluate(settingsTarget.webSocketDebuggerUrl,
        `window.soloSettings.add('http://127.0.0.1:9')`);
    if (refused.ok !== false || readSettings()?.instances?.length) {
        throw new Error('An address that failed its connection test was added anyway.');
    }

    // Driven through the page, as a person would: the opt-out, then the add.
    await evaluate(settingsTarget.webSocketDebuggerUrl, `(() => {
        document.getElementById('native-effects').click();
        document.getElementById('url').value = ${JSON.stringify(fixtureUrl)};
        document.getElementById('add').click();
        return true;
    })()`);
    const persisted = await waitFor(() => {
        const settings = readSettings();
        return settings?.instances?.length === 1 ? settings : null;
    }, 3000, 'The added instance was not persisted.');
    const [instance] = persisted.instances;
    if (persisted.nativeEffects !== false) {
        throw new Error('The native-effects preference did not persist as disabled.');
    }
    if (instance.url !== fixtureUrl || !instance.id || !Number.isInteger(instance.port)) {
        throw new Error('The added instance was not persisted with its address, id and local port.');
    }

    const instanceWindow = await inspectMatching(browserOrigin,
        target => target.type === 'page' && target.url.startsWith(`http://localhost:${instance.port}/`),
        `(async () => ({
            glass: document.documentElement.classList.contains('solo-desktop--glass'),
            instanceUrl: await window.soloDesktop.instanceUrl(),
        }))()`);
    if (instanceWindow.glass) {
        throw new Error('The instance window still enabled native effects after the saved opt-out.');
    }
    if (instanceWindow.instanceUrl !== fixtureUrl) {
        throw new Error('The instance window was not told which instance it is showing.');
    }

    // Once the instance has its window, the settings window closes itself.
    await waitFor(async () => {
        const response = await fetch(`${browserOrigin}/json/list`);
        return !(await response.json()).some(target => target.url.endsWith('/settings/settings.html'));
    }, 3000, 'The settings window stayed open after the added instance opened.');

    console.log('PASS: settings uses native effects by default, persists the opt-out, and adds an instance into its own window.');
} catch (error) {
    if (output.trim()) console.error(output.trim());
    console.error(`FAIL: ${error.message}`);
    process.exitCode = 1;
} finally {
    if (child.exitCode === null && child.signalCode === null) {
        const closed = new Promise(resolve => child.once('close', resolve));
        child.kill('SIGINT');
        await closed;
    }
    fixture.close();
    rmSync(userDataDir, { recursive: true, force: true });
}

function readSettings() {
    try {
        return JSON.parse(readFileSync(path.join(userDataDir, 'settings.json'), 'utf8'));
    } catch {
        return null;
    }
}

function listen(server) {
    return new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => resolve(server.address().port));
    });
}

async function targetMatching(origin, predicate) {
    return waitFor(async () => {
        const response = await fetch(`${origin}/json/list`);
        return (await response.json()).find(predicate);
    }, 5000, 'The expected Solo window did not open.');
}

async function inspectMatching(origin, predicate, expression) {
    const inspection = await waitFor(async () => {
        const response = await fetch(`${origin}/json/list`);
        const target = (await response.json()).find(predicate);
        if (!target) return null;

        // A target is listed just before its debugger socket becomes stable,
        // and navigation can replace that socket. Resolve it again on every
        // attempt instead of holding a stale URL after an arbitrary delay.
        return { value: await evaluate(target.webSocketDebuggerUrl, expression) };
    }, 5000, 'The expected Solo window could not be inspected.');
    return inspection.value;
}

async function waitFor(probe, timeoutMs, message) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        try {
            const value = await probe();
            if (value) return value;
        } catch {
            // Windows and the debugger endpoint become available independently.
        }
        await new Promise(resolve => setTimeout(resolve, 50));
    }
    throw new Error(message);
}

function evaluate(socketUrl, expression) {
    return new Promise((resolve, reject) => {
        const socket = new WebSocket(socketUrl);
        socket.addEventListener('error', () => reject(new Error('Could not inspect the isolated window.')));
        socket.addEventListener('open', () => socket.send(JSON.stringify({
            id: 1,
            method: 'Runtime.evaluate',
            params: { expression, awaitPromise: true, returnByValue: true },
        })));
        socket.addEventListener('message', event => {
            const message = JSON.parse(event.data);
            if (message.id !== 1) return;
            socket.close();
            if (message.result.exceptionDetails) {
                reject(new Error(message.result.exceptionDetails.text));
                return;
            }
            resolve(message.result.result.value);
        });
    });
}
