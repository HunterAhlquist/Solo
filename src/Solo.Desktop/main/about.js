// The About window. It stands in for macOS's own About panel (the `about` menu
// role), which is AppKit and cannot show a page — and this one hides a game:
// three clicks on the logo swap it for a tiny falling-blocks game (about/about.js). The
// layout copies the native panel's so the swap is the only thing that looks
// different.
const { app, BrowserWindow, ipcMain, nativeTheme } = require('electron');
const path = require('path');

const isMac = process.platform === 'darwin';
const WIDTH = 300;
// The page asks for its own height whenever its content changes size. That is
// renderer input, so it is held to sizes an About window could plausibly be.
const MIN_HEIGHT = 120;
const MAX_HEIGHT = 600;

let aboutWindow = null;

// `webPreferences` is the shell's hardened set, passed in so there is one copy
// of it (main.js).
function openAboutWindow(webPreferences) {
    if (aboutWindow) {
        aboutWindow.focus();
        return;
    }

    const win = aboutWindow = new BrowserWindow({
        width: WIDTH,
        // What the page measures on macOS, so its first fit is a no-op there.
        height: 212,
        useContentSize: true,
        resizable: false,
        minimizable: false,
        maximizable: false,
        fullscreenable: false,
        show: false,
        title: 'About Solo',
        // The page's own background, so nothing flashes before it paints.
        backgroundColor: nativeTheme.shouldUseDarkColors ? '#262628' : '#f2f2f4',
        // The native panel has no visible title bar, only the traffic lights.
        ...(isMac ? { titleBarStyle: 'hidden' } : { autoHideMenuBar: true }),
        webPreferences: {
            preload: path.join(__dirname, '..', 'preload', 'about.js'),
            ...webPreferences,
        },
    });

    win.once('ready-to-show', () => win.show());
    win.on('closed', () => {
        if (aboutWindow === win) {
            aboutWindow = null;
        }
    });
    // A local page with no links: it navigates nowhere and opens nothing.
    win.webContents.on('will-navigate', event => event.preventDefault());
    win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    win.loadFile(path.join(__dirname, '..', 'about', 'about.html'), {
        query: { version: app.getVersion(), copyright: copyright() },
    });
}

// What electron-builder writes into the mac bundle by default, which is what
// the native panel used to show. npm's author is "Name <email> (url)" or an
// object with a name; only the name is wanted. The page sets it as text.
function copyright() {
    const { author } = require('../package.json');
    const name = (typeof author === 'object' ? author?.name ?? '' : String(author ?? ''))
        .split(/[<(]/)[0]
        .trim();
    return name ? `Copyright © ${new Date().getFullYear()} ${name}` : '';
}

function registerAboutIpc() {
    ipcMain.on('about:fit', (event, height) => {
        const win = aboutWindow;
        if (!win || win.isDestroyed() || event.sender !== win.webContents ||
            typeof height !== 'number' || !Number.isFinite(height)) {
            return;
        }

        const target = Math.round(Math.min(MAX_HEIGHT, Math.max(MIN_HEIGHT, height)));
        if (target !== win.getContentSize()[1]) {
            // Animated once it's on screen, so the game opening grows the
            // window rather than snapping it. The top edge stays put.
            win.setContentSize(WIDTH, target, win.isVisible());
        }
    });
}

module.exports = { openAboutWindow, registerAboutIpc };
