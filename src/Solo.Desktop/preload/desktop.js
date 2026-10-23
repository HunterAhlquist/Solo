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

    // A right-click menu the page draws itself on the web, shown as a native
    // menu instead: items are { id, label, enabled, separatorBefore }, and the
    // promise settles with the chosen id, or null once it closes without one.
    showContextMenu: items => ipcRenderer.invoke('context-menu:show', items),
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

// How opaque the theme's page background is over the glass, as the percentage
// app.css mixes with. Main has already clamped the number to [0, 1].
const BACKGROUND_ALPHA_ARGUMENT = '--solo-glass-background-alpha=';
let backgroundAlpha = toPercentage(Number(process.argv
    .find(argument => argument.startsWith(BACKGROUND_ALPHA_ARGUMENT))
    ?.slice(BACKGROUND_ALPHA_ARGUMENT.length)));

function toPercentage(alpha) {
    return Number.isFinite(alpha)
        ? `${Math.round(Math.min(1, Math.max(0, alpha)) * 100)}%`
        : '0%';
}

function applyChrome() {
    const root = document.documentElement;
    if (!root) {
        return;
    }

    root.classList.add('solo-desktop', platformClass);
    root.classList.toggle('solo-desktop--fullscreen', fullScreen);
    root.classList.toggle('solo-desktop--glass', glassBackdrop);
    root.style.setProperty('--solo-glass-background-alpha', backgroundAlpha);
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

// Live from the settings window: the slider previews on this window as it
// moves, and main sends the saved value again if the change is abandoned.
ipcRenderer.on('window:glass-background-alpha', (_event, value) => {
    backgroundAlpha = toPercentage(value);
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

// Pass data alone into the page; the renderer never receives the IPC event or
// an Electron object. Canvases opt in and remove their listener on disposal.
ipcRenderer.on('window:rotate-gesture', (_event, value) => {
    if (![value?.rotation, value?.clientX, value?.clientY].every(Number.isFinite)) return;
    window.dispatchEvent(new CustomEvent('solo:rotate-gesture', {
        detail: { rotation: value.rotation, clientX: value.clientX, clientY: value.clientY },
    }));
});
