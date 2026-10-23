import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { EventEmitter } from 'node:events';
import Module, { createRequire } from 'node:module';
import net from 'node:net';
import { fileURLToPath } from 'node:url';

const childFlag = '--recovery-child';
const unexpectedChildFlag = '--unexpected-child';

if (process.argv.includes(childFlag)) {
    await runChild({ connectionReset: true });
} else if (process.argv.includes(unexpectedChildFlag)) {
    await runChild({ connectionReset: false });
} else {
    const childOptions = {
        cwd: new URL('..', import.meta.url),
        encoding: 'utf8',
        env: { ...process.env, SOLO_DISABLE_LIQUID_GLASS: '1' },
    };
    const result = spawnSync(
        process.execPath,
        [fileURLToPath(import.meta.url), childFlag],
        childOptions,
    );
    const output = `${result.stdout}${result.stderr}`;

    assert.equal(
        result.status,
        0,
        `Electron main process did not suppress and recover from ECONNRESET:\n${output}`,
    );
    assert.match(output, /RECOVERED: suppressed ECONNRESET and reloaded on focus/);

    const unexpected = spawnSync(
        process.execPath,
        [fileURLToPath(import.meta.url), unexpectedChildFlag],
        childOptions,
    );
    const unexpectedOutput = `${unexpected.stdout}${unexpected.stderr}`;
    assert.notEqual(unexpected.status, 0, 'An unrelated uncaught exception was suppressed.');
    assert.match(unexpectedOutput, /Error: unexpected recovery-check failure/);
    console.log('Desktop main process suppressed ECONNRESET and reloaded on focus.');
}

async function runChild({ connectionReset }) {
    const require = createRequire(import.meta.url);
    const originalLoad = Module._load;
    let mainWindow = null;
    let reloadCount = 0;

    class FakeWebContents extends EventEmitter {
        isDestroyed() { return false; }
        isLoading() { return false; }
        reload() { reloadCount++; }
        send() {}
        setWindowOpenHandler() {}
    }

    class FakeBrowserWindow extends EventEmitter {
        constructor() {
            super();
            this.webContents = new FakeWebContents();
            mainWindow = this;
        }

        focus() { this.emit('focus'); }
        isMinimized() { return false; }
        loadURL() {}
        setMenuBarVisibility() {}
        setTitle() {}
        setWindowButtonVisibility() {}
        show() {}
    }

    const app = Object.assign(new EventEmitter(), {
        focus() {},
        getName: () => 'Solo',
        getVersion: () => '0.1.0',
        quit() {},
        requestSingleInstanceLock: () => true,
        setPath() {},
        userAgentFallback: 'Mozilla/5.0 Solo/0.1.0 Electron/43.4.1',
        whenReady: () => Promise.resolve(),
    });
    const fakeServer = Object.assign(new EventEmitter(), {
        close: callback => callback(),
        listen: (_port, _host, callback) => callback(),
    });
    const electron = {
        app,
        BrowserWindow: FakeBrowserWindow,
        dialog: { showErrorBox() {} },
        ipcMain: { handle() {}, on() {} },
        Menu: { setApplicationMenu() {} },
        nativeTheme: { shouldUseDarkColors: false, themeSource: 'system' },
        net: { fetch() { throw new Error('Unexpected fetch in recovery check.'); } },
        shell: { openExternal() {} },
    };

    Module._load = function (request, parent, isMain) {
        if (request === 'electron') return electron;
        if (request === './main/settings') {
            return {
                load: () => ({
                    instances: [{
                        id: 'recovery-check',
                        name: '127.0.0.1:9',
                        url: 'http://127.0.0.1:9',
                        port: 41321,
                    }],
                    lastInstanceId: 'recovery-check',
                    nativeEffects: false,
                    port: 41321,
                }),
            };
        }
        if (request === './main/server') {
            return { createSoloServer: () => fakeServer };
        }
        if (request === './main/menu') {
            return { buildMenu: () => null };
        }
        if (request === './main/context-menu') {
            return { attachContextMenu() {} };
        }
        return originalLoad.call(this, request, parent, isMain);
    };

    require('../main.js');
    await waitFor(() => mainWindow, 'Solo did not create its main window.');

    if (!connectionReset) {
        setImmediate(() => { throw new Error('unexpected recovery-check failure'); });
        await delay(100);
        throw new Error('The unexpected exception did not terminate the child.');
    }

    const resetServer = net.createServer(socket => socket.resume());
    await listen(resetServer);
    const client = net.connect(resetServer.address().port, '127.0.0.1');
    client.on('error', () => {});
    await new Promise(resolve => client.once('connect', resolve));
    client.resetAndDestroy();

    await delay(25);
    mainWindow.emit('focus');
    await delay(25);

    assert.equal(reloadCount, 1, 'The focused main window did not reload after ECONNRESET.');
    await close(resetServer);
    console.log('RECOVERED: suppressed ECONNRESET and reloaded on focus');
}

async function waitFor(probe, message) {
    for (let attempt = 0; attempt < 100; attempt++) {
        const value = probe();
        if (value) return value;
        await delay(5);
    }
    throw new Error(message);
}

function delay(milliseconds) {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
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
