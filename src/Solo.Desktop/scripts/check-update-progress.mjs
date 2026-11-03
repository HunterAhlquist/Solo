// What the user sees between pressing Download and the restart offer. Without
// the progress window the update dialog closed and nothing seemed to happen
// until the download finished, so this pins the window opening, following the
// download, and getting out of the way of whatever dialog comes next — and
// that Cancel stops the download without an error dialog about it.
//
// electron and electron-updater are both stubbed through the require cache, as
// in check-updater.mjs: the real ones only load inside a packaged Electron app.
import { createRequire } from 'node:module';
import { EventEmitter } from 'node:events';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const desktopDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const require = createRequire(path.join(desktopDir, 'package.json'));
const { CancellationToken, CancellationError } = require('builder-util-runtime');

const windows = [];
class FakeWindow extends EventEmitter {
    constructor(options) {
        super();
        this.options = options;
        this.destroyed = false;
        this.sent = [];
        this.webContents = Object.assign(new EventEmitter(), {
            send: (channel, payload) => this.sent.push({ channel, payload }),
            setWindowOpenHandler: () => {},
        });
        windows.push(this);
    }

    loadFile(file) { this.file = file; this.webContents.emit('did-finish-load'); }
    isDestroyed() { return this.destroyed; }
    focus() { this.focused = true; }
    setProgressBar() {}
    close() {
        this.destroyed = true;
        this.emit('closed');
    }
}

// Each dialog is answered with the next queued button index.
const dialogs = [];
const answers = [];
const dialog = {
    showMessageBox: async (...args) => {
        dialogs.push(args.at(-1));
        return { response: answers.shift() ?? 1 };
    },
};

const ipcHandlers = new Map();
const app = { isPackaged: true, getVersion: () => '1.18.0', getName: () => 'Solo' };
require.cache[require.resolve('electron')] = {
    id: 'electron',
    filename: 'electron',
    loaded: true,
    exports: {
        app,
        dialog,
        BrowserWindow: FakeWindow,
        ipcMain: { on: (channel, handler) => ipcHandlers.set(channel, handler) },
        nativeTheme: { shouldUseDarkColors: false },
    },
};

// A download the test finishes, fails or lets the user cancel.
let download = null;
const autoUpdater = Object.assign(new EventEmitter(), {
    checkForUpdates: async () => {},
    downloadUpdate: token => new Promise((resolve, reject) => {
        download = { resolve, reject, token };
        token.onCancel(() => reject(new CancellationError()));
    }),
});
require.cache[require.resolve('electron-updater')] = {
    id: 'electron-updater',
    filename: 'electron-updater',
    loaded: true,
    exports: { autoUpdater, CancellationToken },
};

const resources = mkdtempSync(path.join(os.tmpdir(), 'solo-update-progress-'));
writeFileSync(path.join(resources, 'app-update.yml'), 'provider: generic\n');
const originalResources = process.resourcesPath;
const originalPlatform = process.platform;
Object.defineProperty(process, 'resourcesPath', { value: resources, configurable: true });
Object.defineProperty(process, 'platform', { value: 'darwin', configurable: true });

const settle = () => new Promise(resolve => setImmediate(resolve));
const progressWindows = () => windows.filter(win => win.file?.endsWith('update-progress.html'));
const openProgressWindow = () => progressWindows().find(win => !win.destroyed);

async function acceptUpdate(version) {
    answers.push(0);
    autoUpdater.emit('update-available', { version, files: [{ size: 150 * 1024 * 1024 }] });
    await settle();
}

try {
    const { registerUpdater, checkForUpdates } = require(path.join(desktopDir, 'main', 'updater.js'));
    const webPreferences = { sandbox: true, contextIsolation: true };
    registerUpdater({ getWindow: () => null, webPreferences });

    // Accepting opens the window straight away, before electron-updater has
    // reported a byte, and with the shell's hardened preferences.
    await acceptUpdate('1.19.0');
    const win = openProgressWindow();
    assert.ok(win, 'pressing Download must open the progress window');
    assert.equal(win.options.webPreferences.sandbox, true);
    assert.match(win.options.webPreferences.preload, /preload[\\/]update-progress\.js$/);
    assert.deepEqual(win.sent.at(-1).payload,
        { name: 'Solo', version: '1.19.0', transferred: 0, total: 0, bytesPerSecond: 0, percent: 0 });

    autoUpdater.emit('download-progress',
        { transferred: 60 * 1024 * 1024, total: 150 * 1024 * 1024, bytesPerSecond: 3 * 1024 * 1024, percent: 40 });
    assert.equal(win.sent.at(-1).channel, 'update-progress:state');
    assert.equal(win.sent.at(-1).payload.percent, 40);
    assert.equal(win.sent.at(-1).payload.version, '1.19.0');

    // Closing the window is not cancelling: the download carries on, and the
    // menu brings the window back with the figures it had reached.
    win.close();
    assert.equal(download.token.cancelled, false, 'closing the window must not cancel the download');
    await checkForUpdates({ fromMenu: true, getWindow: () => null });
    const reopened = openProgressWindow();
    assert.ok(reopened && reopened !== win, 'Check for Updates… must reopen the progress window');
    assert.equal(reopened.sent.at(-1).payload.percent, 40);

    // Finishing closes the window before the restart offer appears.
    const dialogsBefore = dialogs.length;
    autoUpdater.emit('update-downloaded', { version: '1.19.0' });
    await settle();
    assert.equal(openProgressWindow(), undefined, 'the progress window must close once the update is downloaded');
    assert.match(dialogs[dialogsBefore].message, /ready to install/);
    download.resolve([]);
    await settle();

    // Cancel stops the download and says nothing more.
    await acceptUpdate('1.19.1');
    const cancelled = openProgressWindow();
    const cancelDownload = download;
    const dialogsBeforeCancel = dialogs.length;
    ipcHandlers.get('update-progress:cancel')({ sender: cancelled.webContents });
    await settle();
    assert.equal(cancelDownload.token.cancelled, true, 'Cancel must cancel the download');
    assert.equal(openProgressWindow(), undefined);
    assert.equal(dialogs.length, dialogsBeforeCancel, 'a cancelled download must not raise an error dialog');

    // Only the progress page itself may cancel.
    await acceptUpdate('1.19.2');
    ipcHandlers.get('update-progress:cancel')({ sender: new EventEmitter() });
    assert.equal(download.token.cancelled, false, 'another renderer must not be able to cancel the download');

    // A failure closes the window and is reported, because the user asked for
    // this download and is waiting on it.
    download.reject(new Error('socket hang up'));
    await settle();
    assert.equal(openProgressWindow(), undefined);
    assert.match(dialogs.at(-1).message, /couldn’t be downloaded/);
    assert.equal(dialogs.at(-1).detail, 'socket hang up');

    console.log(
        'PASS: the progress window opens on Download, follows the download, closes before ' +
        'the restart offer or an error, can be reopened from the menu, and Cancel stops ' +
        'the download silently.');
} finally {
    Object.defineProperty(process, 'resourcesPath', { value: originalResources, configurable: true });
    Object.defineProperty(process, 'platform', { value: originalPlatform, configurable: true });
    rmSync(resources, { recursive: true, force: true });
}
