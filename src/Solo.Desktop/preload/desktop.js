// The client's entire desktop-shell capability surface. Its presence is also
// the detection signal: the client's solo-interop.js treats a page with
// window.soloDesktop as running inside the shell.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('soloDesktop', {
    // Opens the browser sign-in flow (main mints the state nonce).
    beginSignIn: () => ipcRenderer.invoke('desktop-auth:begin'),
});
