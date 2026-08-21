import { spawn } from 'node:child_process';
import { createServer as createHttpServer } from 'node:http';
import { createServer as createNetServer } from 'node:net';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

if (process.platform !== 'darwin') {
    console.log('SKIP: native appearance synchronization is macOS-only.');
    process.exit(0);
}

const desktopDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const electron = process.env.SOLO_GLASS_APP ?? path.join(desktopDir, 'node_modules', '.bin', 'electron');
const userDataDir = mkdtempSync(path.join(os.tmpdir(), 'solo-glass-theme-check-'));
const localPort = await availablePort();
const fixture = createHttpServer((request, response) => {
    response.setHeader('Content-Type', 'application/json');
    if (request.url === '/api/config/auth') {
        response.end('{}');
        return;
    }

    response.statusCode = 401;
    response.end('{"error":"unauthorized fixture request"}');
});
const fixturePort = await listen(fixture);
writeFileSync(path.join(userDataDir, 'settings.json'), JSON.stringify({
    instanceUrl: `http://127.0.0.1:${fixturePort}`,
    port: localPort,
}));

const env = { ...process.env, SOLO_USER_DATA_DIR: userDataDir };
delete env.SOLO_DISABLE_LIQUID_GLASS;
delete env.SOLO_GLASS_APP;

const child = spawn(electron, ['--remote-debugging-port=0', '.'], {
    cwd: desktopDir,
    env,
    stdio: ['ignore', 'pipe', 'pipe'],
});

let output = '';
let finished = false;

function collect(chunk) {
    output += chunk.toString();
}

child.stdout.on('data', collect);
child.stderr.on('data', collect);
child.on('error', error => {
    output += `Electron launch failed: ${error.message}\n`;
});

try {
    const browserSocket = await waitFor(() => {
        const match = output.match(/DevTools listening on (ws:\/\/[^\s]+)/);
        return match?.[1];
    }, 5000, 'Electron did not expose its test debugger.');
    const browserOrigin = browserSocket.replace(/^ws:/, 'http:').replace(/\/devtools\/browser\/.*$/, '');
    const target = await waitFor(async () => {
        const response = await fetch(`${browserOrigin}/json/list`);
        const targets = await response.json();
        return targets.find(candidate => candidate.type === 'page' && candidate.url.startsWith(`http://localhost:${localPort}/`));
    }, 5000, 'Solo did not create its isolated renderer.');
    // The debugger target becomes visible during navigation, just before the
    // final document's preload bridge is installed.
    await new Promise(resolve => setTimeout(resolve, 250));
    await waitFor(async () => evaluate(target.webSocketDebuggerUrl,
        `getComputedStyle(document.documentElement)
            .getPropertyValue('--mud-palette-background').trim() || null`),
        5000, 'The isolated Blazor theme provider did not initialize.');

    const bridgeTransitions = await evaluate(target.webSocketDebuggerUrl, `(async () => {
        const query = matchMedia('(prefers-color-scheme: dark)');
        const systemDark = query.matches;

        async function apply(source, expectedDark) {
            const expectedScheme = expectedDark ? 'dark' : 'light';
            const paletteIsDark = () => {
                const channels = getComputedStyle(document.documentElement)
                    .getPropertyValue('--mud-palette-background').match(/[\\d.]+/g)?.slice(0, 3).map(Number) ?? [];
                return channels.length === 3 && channels.reduce((sum, channel) => sum + channel, 0) / 3 < 128;
            };
            if (typeof window.soloDesktop?.setThemeSource !== 'function') {
                return {
                    source,
                    expectedDark,
                    effectiveDark: query.matches,
                    resolvedScheme: document.documentElement.dataset.colorScheme,
                    paletteDark: paletteIsDark(),
                    bridgeKeys: Object.keys(window.soloDesktop ?? {}),
                    error: 'desktop theme bridge is missing'
                };
            }

            window.soloDesktop.setThemeSource(source);
            for (let attempt = 0; attempt < 40 &&
                (query.matches !== expectedDark ||
                    document.documentElement.dataset.colorScheme !== expectedScheme ||
                    paletteIsDark() !== expectedDark); attempt++) {
                await new Promise(resolve => setTimeout(resolve, 25));
            }
            return {
                source,
                expectedDark,
                effectiveDark: query.matches,
                expectedScheme,
                resolvedScheme: document.documentElement.dataset.colorScheme,
                paletteDark: paletteIsDark(),
                paletteBackground: getComputedStyle(document.documentElement)
                    .getPropertyValue('--mud-palette-background').trim()
            };
        }

        const transitions = [];
        transitions.push(await apply('light', false));
        transitions.push(await apply('dark', true));
        transitions.push(await apply('system', systemDark));
        return transitions;
    })()`);

    const systemDark = bridgeTransitions.at(-1).effectiveDark;
    const bootTransitions = [];
    for (const [preference, expectedDark] of [['Light', false], ['Dark', true], ['System', systemDark]]) {
        await evaluate(target.webSocketDebuggerUrl,
            `localStorage.setItem('solo.colorScheme', ${JSON.stringify(preference)}); true`);
        await command(target.webSocketDebuggerUrl, 'Page.reload', { ignoreCache: true });
        const state = await waitFor(async () => {
            const current = await evaluate(target.webSocketDebuggerUrl, `({
                ready: document.readyState,
                preference: localStorage.getItem('solo.colorScheme'),
                effectiveDark: matchMedia('(prefers-color-scheme: dark)').matches
            })`);
            return current.ready === 'complete' && current.preference === preference ? current : null;
        }, 3000, `Solo did not finish reloading in ${preference} mode.`);
        bootTransitions.push({ preference, expectedDark, effectiveDark: state.effectiveDark });
    }

    const result = { bridgeTransitions, bootTransitions };
    console.log(JSON.stringify(result, null, 2));
    const failed = [
        ...bridgeTransitions.filter(item => item.error ||
            item.expectedDark !== item.effectiveDark ||
            item.expectedDark !== item.paletteDark ||
            item.expectedScheme !== item.resolvedScheme),
        ...bootTransitions.filter(item => item.expectedDark !== item.effectiveDark),
    ];
    if (failed.length) {
        throw new Error(`${failed.map(item => item.source ?? item.preference).join(', ')} did not synchronize the app and Electron appearance.`);
    }

    console.log('PASS: Electron follows live app-theme and system-theme source changes.');
    finished = true;
} catch (error) {
    console.error(output.trim());
    console.error(`FAIL: ${error.message}`);
    process.exitCode = 1;
} finally {
    if (child.exitCode === null && child.signalCode === null) {
        const closed = new Promise(resolve => child.once('close', resolve));
        child.kill('SIGINT');
        await closed;
    }
    await new Promise(resolve => fixture.close(resolve));
    rmSync(userDataDir, { recursive: true, force: true });
}

if (!finished && process.exitCode !== 1) {
    process.exitCode = 1;
}

function availablePort() {
    return new Promise((resolve, reject) => {
        const server = createNetServer();
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => {
            const address = server.address();
            server.close(error => error ? reject(error) : resolve(address.port));
        });
    });
}

function listen(server) {
    return new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => resolve(server.address().port));
    });
}

async function waitFor(probe, timeoutMs, message) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        try {
            const value = await probe();
            if (value) return value;
        } catch {
            // The renderer and debugger endpoints come up independently.
        }
        await new Promise(resolve => setTimeout(resolve, 50));
    }
    throw new Error(message);
}

function evaluate(socketUrl, expression) {
    return command(socketUrl, 'Runtime.evaluate', {
        expression,
        awaitPromise: true,
        returnByValue: true,
    }).then(result => {
        if (result.exceptionDetails) {
            throw new Error(result.exceptionDetails.text);
        }
        return result.result.value;
    });
}

function command(socketUrl, method, params = {}) {
    return new Promise((resolve, reject) => {
        const socket = new WebSocket(socketUrl);
        socket.addEventListener('error', () => reject(new Error('Could not connect to the isolated renderer.')));
        socket.addEventListener('open', () => socket.send(JSON.stringify({
            id: 1,
            method,
            params,
        })));
        socket.addEventListener('message', event => {
            const message = JSON.parse(event.data);
            if (message.id !== 1) return;
            socket.close();
            resolve(message.result);
        });
    });
}
