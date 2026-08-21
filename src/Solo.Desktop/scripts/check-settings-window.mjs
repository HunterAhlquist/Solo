import { spawn } from 'node:child_process';
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
        hasBridge: typeof window.soloSettings?.save === 'function',
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
    if (!appearance.hasBridge || !appearance.glass || appearance.controlHidden ||
        !appearance.controlChecked || appearance.backdrop === 'none' ||
        appearance.dragRegion !== 'drag' || appearance.inputRegion !== 'no-drag' ||
        !appearance.glassWash.includes('/ 48%')) {
        throw new Error('The settings window did not start with its native appearance, drag regions, and enabled control.');
    }

    await evaluate(settingsTarget.webSocketDebuggerUrl,
        `window.soloSettings.save('http://127.0.0.1:9', false); true`);
    const persisted = await waitFor(() => {
        try {
            return JSON.parse(readFileSync(path.join(userDataDir, 'settings.json'), 'utf8'));
        } catch {
            return null;
        }
    }, 3000, 'The native-effects preference was not persisted.');
    if (persisted.nativeEffects !== false) {
        throw new Error('The native-effects preference did not persist as disabled.');
    }

    const mainTarget = await targetMatching(browserOrigin,
        target => target.type === 'page' && target.url.startsWith('http://localhost:41321/'));
    await new Promise(resolve => setTimeout(resolve, 250));
    const mainUsesGlass = await evaluate(mainTarget.webSocketDebuggerUrl,
        `document.documentElement.classList.contains('solo-desktop--glass')`);
    if (mainUsesGlass) {
        throw new Error('The main window still enabled native effects after the saved opt-out.');
    }

    console.log('PASS: settings uses native effects by default and persists the opt-out.');
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
    rmSync(userDataDir, { recursive: true, force: true });
}

async function targetMatching(origin, predicate) {
    return waitFor(async () => {
        const response = await fetch(`${origin}/json/list`);
        return (await response.json()).find(predicate);
    }, 5000, 'The expected Solo window did not open.');
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
