// The window an update downloads in. The download is something the user asked
// for and is waiting on, and without this the only sign of it was the dock or
// taskbar icon's progress bar — easy to miss, and absent entirely on Linux — so
// the update dialog closed and nothing seemed to happen until the restart offer.
//
// Closing the window does not cancel the download; only Cancel does. Closing
// is "stop showing me this", and Check for Updates… brings it back.
const { app, BrowserWindow, ipcMain, nativeTheme } = require('electron');
const path = require('path');

const isMac = process.platform === 'darwin';

let progressWindow = null;
let onCancel = null;
// What the page was last told, so a page that is still loading — or a window
// reopened mid-download — starts from the current figures, not from zero.
let latest = null;

// `version` is the update's, for the heading. `cancel` is called when the user
// presses Cancel. `webPreferences` is the shell's hardened set (main.js).
function startUpdateProgress({ version, cancel, webPreferences }) {
    onCancel = cancel;
    latest = { name: app.getName(), version, transferred: 0, total: 0, bytesPerSecond: 0, percent: 0 };
    showUpdateProgress(webPreferences);
}

// Brings back a window the user closed while the download carries on.
function showUpdateProgress(webPreferences) {
    if (!latest) {
        return;
    }

    if (progressWindow) {
        progressWindow.focus();
        return;
    }

    const win = progressWindow = new BrowserWindow({
        width: 420,
        height: 124,
        useContentSize: true,
        resizable: false,
        minimizable: false,
        maximizable: false,
        fullscreenable: false,
        show: false,
        title: `Updating ${app.getName()}`,
        backgroundColor: nativeTheme.shouldUseDarkColors ? '#262628' : '#f2f2f4',
        ...(isMac ? { titleBarStyle: 'hidden' } : { autoHideMenuBar: true }),
        webPreferences: {
            preload: path.join(__dirname, '..', 'preload', 'update-progress.js'),
            ...webPreferences,
        },
    });

    win.once('ready-to-show', () => win.show());
    win.webContents.on('did-finish-load', () => send(latest));
    win.on('closed', () => {
        if (progressWindow === win) {
            progressWindow = null;
        }
    });
    win.webContents.on('will-navigate', event => event.preventDefault());
    win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    win.loadFile(path.join(__dirname, '..', 'update', 'update-progress.html'));
}

// electron-updater's download-progress payload: bytes so far, total bytes,
// bytes per second and percent.
function reportUpdateProgress(progress) {
    if (!latest) {
        return;
    }

    latest = {
        name: latest.name,
        version: latest.version,
        transferred: progress?.transferred ?? 0,
        total: progress?.total ?? 0,
        bytesPerSecond: progress?.bytesPerSecond ?? 0,
        percent: progress?.percent ?? 0,
    };
    send(latest);
}

// The download finished, failed or was cancelled: whatever comes next is a
// dialog of its own, so this window has nothing more to say.
function closeUpdateProgress() {
    latest = null;
    onCancel = null;
    // Let go of it now rather than on 'closed', which is asynchronous: a new
    // download starting in between would otherwise reuse a closing window.
    const win = progressWindow;
    progressWindow = null;
    if (win && !win.isDestroyed()) {
        win.close();
    }
}

function send(state) {
    if (state && progressWindow && !progressWindow.isDestroyed()) {
        progressWindow.webContents.send('update-progress:state', state);
    }
}

function registerUpdateProgressIpc() {
    ipcMain.on('update-progress:cancel', event => {
        if (!progressWindow || event.sender !== progressWindow.webContents) {
            return;
        }

        const cancel = onCancel;
        closeUpdateProgress();
        cancel?.();
    });
}

module.exports = {
    startUpdateProgress,
    showUpdateProgress,
    reportUpdateProgress,
    closeUpdateProgress,
    registerUpdateProgressIpc,
};
