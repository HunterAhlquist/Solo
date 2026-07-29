// The client's entire desktop-shell capability surface. Its presence is also
// the detection signal: the client's solo-interop.js treats a page with
// window.soloDesktop as running inside the shell.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('soloDesktop', {
    // Opens the browser sign-in flow (main starts a device-code flow on the
    // instance and polls it for the session).
    beginSignIn: () => ipcRenderer.invoke('desktop-auth:begin'),
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

function applyChrome() {
    const root = document.documentElement;
    if (!root) {
        return;
    }

    root.classList.add('solo-desktop', platformClass);
    root.classList.toggle('solo-desktop--fullscreen', fullScreen);
}

ipcRenderer.on('window:full-screen', (_event, value) => {
    fullScreen = value === true;
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
