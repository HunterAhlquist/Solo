import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import {
    copyFileSync,
    mkdirSync,
    mkdtempSync,
    rmSync,
    writeFileSync,
} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

if (process.platform !== 'darwin') {
    console.log('SKIP: the glass loading view is macOS-only.');
    process.exit(0);
}

const desktopDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const clientDir = path.resolve(desktopDir, '..', 'Solo.Client', 'wwwroot');
const electron = process.env.SOLO_GLASS_APP ?? path.join(desktopDir, 'node_modules', '.bin', 'electron');
const userDataDir = mkdtempSync(path.join(os.tmpdir(), 'solo-glass-loading-profile-'));
const fixtureDir = mkdtempSync(path.join(os.tmpdir(), 'solo-glass-loading-client-'));
const localPort = await availablePort();

mkdirSync(path.join(fixtureDir, 'css'));
mkdirSync(path.join(fixtureDir, 'js'));
copyFileSync(path.join(clientDir, 'index.html'), path.join(fixtureDir, 'index.html'));
copyFileSync(path.join(clientDir, 'css', 'app.css'), path.join(fixtureDir, 'css', 'app.css'));
copyFileSync(path.join(clientDir, 'js', 'theme-boot.js'), path.join(fixtureDir, 'js', 'theme-boot.js'));
writeFileSync(path.join(userDataDir, 'settings.json'), JSON.stringify({
    instanceUrl: 'http://127.0.0.1:9',
    port: localPort,
}));

const env = {
    ...process.env,
    SOLO_CLIENT_DIR: fixtureDir,
    SOLO_USER_DATA_DIR: userDataDir,
};
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
    const target = await waitFor(async () => {
        const response = await fetch(`${browserOrigin}/json/list`);
        return (await response.json()).find(candidate =>
            candidate.type === 'page' && candidate.url.startsWith(`http://localhost:${localPort}/`));
    }, 5000, 'Solo did not create its isolated loading view.');

    const requestedTheme = process.env.SOLO_GLASS_THEME?.toLowerCase();
    if (requestedTheme === 'light' || requestedTheme === 'dark') {
        await waitFor(() => evaluate(target.webSocketDebuggerUrl,
            `document.readyState === 'complete' && location.origin.startsWith('http://localhost:')`),
            3000, 'The loading view was not ready for a theme reload.');
        const preference = requestedTheme === 'light' ? 'Light' : 'Dark';
        await evaluate(target.webSocketDebuggerUrl,
            `localStorage.setItem('solo.colorScheme', ${JSON.stringify(preference)}); true`);
        await command(target.webSocketDebuggerUrl, 'Page.reload', { ignoreCache: true });
        await new Promise(resolve => setTimeout(resolve, 250));
    }

    const appearance = await waitFor(async () => {
        const value = await evaluate(target.webSocketDebuggerUrl, `(() => {
            const splash = document.querySelector('.boot-splash');
            const panel = document.querySelector('.boot-splash__panel');
            const bar = document.querySelector('.boot-splash__bar');
            const mark = document.querySelector('.boot-splash__mark');
            if (!splash || !bar || !mark) return null;

            function alpha(color) {
                if (color === 'transparent') return 0;
                const slash = color.match(/\\/\\s*([\\d.]+)(%)?\\s*\\)$/);
                if (slash) return Number(slash[1]) / (slash[2] ? 100 : 1);
                const rgba = color.match(/^rgba\\([^,]+,[^,]+,[^,]+,\\s*([\\d.]+)\\)$/);
                return rgba ? Number(rgba[1]) : 1;
            }

            const splashStyle = getComputedStyle(splash);
            const panelStyle = panel ? getComputedStyle(panel) : null;
            const barStyle = getComputedStyle(bar);
            return {
                glass: document.documentElement.classList.contains('solo-desktop--glass'),
                panel: !!panel,
                splashAlpha: alpha(splashStyle.backgroundColor),
                panelAlpha: panelStyle ? alpha(panelStyle.backgroundColor) : 1,
                panelBackdrop: panelStyle ?
                    (panelStyle.backdropFilter || panelStyle.webkitBackdropFilter) : 'none',
                panelRadius: panelStyle ? parseFloat(panelStyle.borderRadius) : 0,
                barAlpha: alpha(barStyle.backgroundColor),
                markShadow: getComputedStyle(mark).textShadow,
            };
        })()`);
        return value?.glass ? value : null;
    }, 5000, 'The desktop glass class was not applied to the loading view.');

    console.log(JSON.stringify({ loadingAppearance: appearance }, null, 2));
    if (!appearance.panel || appearance.splashAlpha >= 0.5 ||
        appearance.panelAlpha < 0.2 || appearance.panelAlpha >= 0.9 ||
        appearance.panelBackdrop === 'none' || appearance.panelRadius < 20 ||
        appearance.barAlpha >= 0.8 || appearance.markShadow === 'none') {
        throw new Error('The loading view did not render its translucent glass material.');
    }

    const browserFallback = await evaluate(target.webSocketDebuggerUrl, `(() => {
        const root = document.documentElement;
        root.classList.remove('solo-desktop--glass');
        const splash = getComputedStyle(document.querySelector('.boot-splash'));
        const panel = getComputedStyle(document.querySelector('.boot-splash__panel'));
        const result = {
            splashBackground: splash.backgroundColor,
            panelBackground: panel.backgroundColor,
            panelBackdrop: panel.backdropFilter || panel.webkitBackdropFilter,
        };
        root.classList.add('solo-desktop--glass');
        return result;
    })()`);
    console.log(JSON.stringify({ browserFallback }, null, 2));
    if (browserFallback.splashBackground === 'rgba(0, 0, 0, 0)' ||
        browserFallback.panelBackground !== 'rgba(0, 0, 0, 0)' ||
        browserFallback.panelBackdrop !== 'none') {
        throw new Error('The desktop loading material leaked into the hosted browser splash.');
    }

    if (process.env.SOLO_GLASS_SCREENSHOT) {
        const screenshot = await command(target.webSocketDebuggerUrl, 'Page.captureScreenshot', {
            format: 'png',
            captureBeyondViewport: false,
        });
        writeFileSync(process.env.SOLO_GLASS_SCREENSHOT, Buffer.from(screenshot.data, 'base64'));
    }

    console.log('PASS: the loading view uses the desktop glass material.');
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
    rmSync(fixtureDir, { recursive: true, force: true });
}

function availablePort() {
    return new Promise((resolve, reject) => {
        const server = createServer();
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => {
            const address = server.address();
            server.close(error => error ? reject(error) : resolve(address.port));
        });
    });
}

async function waitFor(probe, timeoutMs, message) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        try {
            const value = await probe();
            if (value) return value;
        } catch {
            // The target appears before its final document and styles settle.
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
            throw new Error(result.exceptionDetails.exception?.description ?? result.exceptionDetails.text);
        }
        return result.result.value;
    });
}

function command(socketUrl, method, params = {}) {
    return new Promise((resolve, reject) => {
        const socket = new WebSocket(socketUrl);
        socket.addEventListener('error', () => reject(new Error('Could not inspect the loading view.')));
        socket.addEventListener('open', () => socket.send(JSON.stringify({ id: 1, method, params })));
        socket.addEventListener('message', event => {
            const message = JSON.parse(event.data);
            if (message.id !== 1) return;
            socket.close();
            resolve(message.result);
        });
    });
}
