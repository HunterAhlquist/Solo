import { execFileSync, spawn } from 'node:child_process';
import { createServer as createHttpServer } from 'node:http';
import { createServer as createNetServer } from 'node:net';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
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
    const surfaceEffects = await evaluate(target.webSocketDebuggerUrl, `(() => {
        const fixture = document.createElement('div');
        fixture.innerHTML = [
            '<div class="mud-appbar"></div>',
            '<div class="board-header"></div>',
            '<div class="kanban-header"></div>',
            '<div class="mud-drawer detail-drawer"></div>',
            '<div class="kanban-lane"></div>',
            '<div class="kanban-lane kanban-lane--adhoc"></div>'
        ].join('');
        document.body.append(fixture);
        const [appbar, boardHeader, kanbanHeader, drawer, epicLane, adHocLane] = fixture.children;
        const read = element => {
            const style = getComputedStyle(element);
            return {
                background: style.backgroundColor,
                blur: style.backdropFilter || style.webkitBackdropFilter
            };
        };
        const result = {
            appbar: read(appbar),
            boardHeader: read(boardHeader),
            kanbanHeader: read(kanbanHeader),
            drawer: read(drawer),
            epicLane: read(epicLane),
            adHocLane: read(adHocLane)
        };
        fixture.remove();
        return result;
    })()`);
    const backdropDefocus = await measureBackdropDefocus(
        target.webSocketDebuggerUrl, userDataDir);
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

    const result = { bridgeTransitions, bootTransitions, surfaceEffects, backdropDefocus };
    console.log(JSON.stringify(result, null, 2));
    const failed = [
        ...bridgeTransitions.filter(item => item.error ||
            item.expectedDark !== item.effectiveDark ||
            item.expectedDark !== item.paletteDark ||
            item.expectedScheme !== item.resolvedScheme),
        ...bootTransitions.filter(item => item.expectedDark !== item.effectiveDark),
    ];
    if (surfaceEffects.appbar.blur === 'none' || surfaceEffects.boardHeader.blur === 'none' ||
        surfaceEffects.kanbanHeader.blur === 'none' ||
        surfaceEffects.boardHeader.background !== 'rgba(0, 0, 0, 0)' ||
        surfaceEffects.kanbanHeader.background !== 'rgba(0, 0, 0, 0)' ||
        surfaceEffects.drawer.blur === 'none' ||
        surfaceEffects.drawer.background !== surfaceEffects.epicLane.background ||
        surfaceEffects.drawer.blur !== surfaceEffects.epicLane.blur ||
        surfaceEffects.adHocLane.blur !== 'none') {
        failed.push({ source: 'glass surface effects' });
    }
    if (backdropDefocus.actualContrastRatio > 0.15) {
        failed.push({ source: 'appbar backdrop defocus' });
    }
    if (backdropDefocus.exposedContrast < 80) {
        failed.push({ source: 'appbar defocus reference capture' });
    }
    if (backdropDefocus.formerContrastRatio < 0.3) {
        failed.push({ source: 'appbar defocus regression sensitivity' });
    }
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

async function measureBackdropDefocus(socketUrl, directory) {
    await command(socketUrl, 'Page.bringToFront');
    await evaluate(socketUrl, `new Promise(resolve => {
        document.body.innerHTML = [
            '<div id="defocus-pattern"></div>',
            '<div class="board-header" id="defocus-appbar"></div>'
        ].join('');
        document.documentElement.style.background = '#808080';
        document.body.style.cssText = 'margin:0;background:#808080;overflow:hidden';
        const pattern = document.getElementById('defocus-pattern');
        pattern.style.cssText = [
            'position:fixed', 'inset:0 auto auto 0', 'width:480px', 'height:160px',
            'z-index:1',
            'background:repeating-linear-gradient(90deg,#000 0 64px,#fff 64px 128px)'
        ].join(';');
        const appbar = document.getElementById('defocus-appbar');
        appbar.style.cssText = [
            'position:fixed', 'inset:0 auto auto 0', 'width:480px', 'height:160px',
            'z-index:2', 'box-shadow:none', 'border:0'
        ].join(';');
        requestAnimationFrame(() => requestAnimationFrame(resolve));
    })`);

    const actualBitmap = await captureBitmap(socketUrl, directory, 'defocus-actual');
    await evaluate(socketUrl, `new Promise(resolve => {
        const appbar = document.getElementById('defocus-appbar');
        appbar.style.webkitBackdropFilter = 'blur(22px) saturate(125%)';
        appbar.style.backdropFilter = 'blur(22px) saturate(125%)';
        requestAnimationFrame(() => requestAnimationFrame(resolve));
    })`);
    const formerBitmap = await captureBitmap(socketUrl, directory, 'defocus-former');
    await evaluate(socketUrl, `new Promise(resolve => {
        document.getElementById('defocus-appbar').remove();
        requestAnimationFrame(() => requestAnimationFrame(resolve));
    })`);
    const exposedBitmap = await captureBitmap(socketUrl, directory, 'defocus-exposed');
    const scaleX = actualBitmap.width / 480;
    const scaleY = actualBitmap.height / 160;
    const bounds = [40 * scaleX, 440 * scaleX, 20 * scaleY, 140 * scaleY];
    const actual = lumaContrast(actualBitmap, ...bounds);
    const former = lumaContrast(formerBitmap, ...bounds);
    const exposed = lumaContrast(exposedBitmap, ...bounds);
    return {
        bitmap: { width: actualBitmap.width, height: actualBitmap.height },
        actualContrast: Number(actual.toFixed(2)),
        formerContrast: Number(former.toFixed(2)),
        exposedContrast: Number(exposed.toFixed(2)),
        actualContrastRatio: Number((actual / exposed).toFixed(3)),
        formerContrastRatio: Number((former / exposed).toFixed(3)),
    };
}

async function captureBitmap(socketUrl, directory, label) {
    const screenshot = await command(socketUrl, 'Page.captureScreenshot', {
        format: 'png',
        fromSurface: true,
        captureBeyondViewport: false,
        clip: { x: 0, y: 0, width: 480, height: 160, scale: 1 },
    });
    const pngPath = path.join(directory, `${label}.png`);
    const bmpPath = path.join(directory, `${label}.bmp`);
    writeFileSync(pngPath, Buffer.from(screenshot.data, 'base64'));
    execFileSync('sips', ['-s', 'format', 'bmp', pngPath, '--out', bmpPath], {
        stdio: 'ignore',
    });
    return readBitmap(bmpPath);
}

function readBitmap(filePath) {
    const bytes = readFileSync(filePath);
    const offset = bytes.readUInt32LE(10);
    const width = bytes.readInt32LE(18);
    const storedHeight = bytes.readInt32LE(22);
    const bitsPerPixel = bytes.readUInt16LE(28);
    if (width <= 0 || storedHeight === 0 || ![24, 32].includes(bitsPerPixel) ||
        bytes.readUInt32LE(30) !== 0) {
        throw new Error('The defocus screenshot used an unsupported bitmap format.');
    }
    return {
        bytes,
        offset,
        width,
        height: Math.abs(storedHeight),
        bottomUp: storedHeight > 0,
        bytesPerPixel: bitsPerPixel / 8,
        rowStride: Math.ceil(width * bitsPerPixel / 32) * 4,
    };
}

function lumaContrast(bitmap, xStart, xEnd, yStart, yEnd) {
    const samples = [];
    for (let y = Math.round(yStart); y < Math.round(yEnd); y++) {
        for (let x = Math.round(xStart); x < Math.round(xEnd); x++) {
            samples.push(bitmapLuma(bitmap, x, y));
        }
    }
    const mean = samples.reduce((sum, sample) => sum + sample, 0) / samples.length;
    return Math.sqrt(samples.reduce((sum, sample) =>
        sum + (sample - mean) ** 2, 0) / samples.length);
}

function bitmapLuma(bitmap, x, y) {
    x = Math.round(x);
    y = Math.round(y);
    const row = bitmap.bottomUp ? bitmap.height - 1 - y : y;
    const index = bitmap.offset + row * bitmap.rowStride + x * bitmap.bytesPerPixel;
    return 0.2126 * bitmap.bytes[index + 2] +
        0.7152 * bitmap.bytes[index + 1] +
        0.0722 * bitmap.bytes[index];
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
