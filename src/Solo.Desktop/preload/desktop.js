// The client's entire desktop-shell capability surface. Its presence is also
// the detection signal: the client's solo-interop.js treats a page with
// window.soloDesktop as running inside the shell.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('soloDesktop', {
    // Opens the browser sign-in flow. Main receives a one-time code on an
    // ephemeral loopback listener and redeems it with PKCE for the session.
    beginSignIn: () => ipcRenderer.invoke('desktop-auth:begin'),

    // The Solo instance this shell is pointed at. The client needs it whenever
    // it shows an address for something outside the app to use — its own origin
    // is this local server, which only exists while the app is running.
    instanceUrl: () => ipcRenderer.invoke('instance:url'),

    // Electron's native appearance controls Liquid Glass and vibrancy. The
    // renderer supplies the user's three-state Solo preference so an explicit
    // app theme does not accidentally inherit the opposite macOS appearance.
    setThemeSource: source => ipcRenderer.send('window:theme-source', source),
});

// ---------------------------------------------------------------------------
// Window chrome. The shell hides the OS title bar and draws the platform's
// window controls over the client's app bar (see createMainWindow in main.js),
// which only works if the client knows to make that bar a drag region and to
// leave room for the buttons. These classes are that signal — they are set
// here rather than in the client so the hosted web build never sees them; the
// rules they drive live in Solo.Client/wwwroot/css/app.css.
// ---------------------------------------------------------------------------
const platformClass = process.platform === 'darwin'
    ? 'solo-desktop--mac'       // inset traffic lights, left
    : 'solo-desktop--overlay';  // Window Controls Overlay, right

let fullScreen = false;
let glassBackdrop = process.argv.includes('--solo-glass-backdrop');

function applyChrome() {
    const root = document.documentElement;
    if (!root) {
        return;
    }

    root.classList.add('solo-desktop', platformClass);
    root.classList.toggle('solo-desktop--fullscreen', fullScreen);
    root.classList.toggle('solo-desktop--glass', glassBackdrop);
}

ipcRenderer.on('window:full-screen', (_event, value) => {
    fullScreen = value === true;
    applyChrome();
});

// The command-line flag applies the transparent styling before first paint.
// Main turns it back off only if both the native and vibrancy paths fail.
ipcRenderer.on('window:glass-backdrop', (_event, value) => {
    glassBackdrop = value === true;
    applyChrome();
});

ipcRenderer.invoke('window:full-screen').then(value => {
    fullScreen = value === true;
    applyChrome();
});

// Preload runs before the document is parsed, so <html> may not exist yet; on
// a reload it already does, and applying now avoids a frame of the wrong inset.
applyChrome();
document.addEventListener('DOMContentLoaded', applyChrome);
