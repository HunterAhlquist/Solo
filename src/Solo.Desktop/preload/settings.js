// The settings page's entire capability surface: read the current settings,
// test a candidate URL, preview a background opacity on the app window, save,
// or close the window. Nothing else crosses the bridge.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('soloSettings', {
    get: () => ipcRenderer.invoke('settings:get'),
    test: url => ipcRenderer.invoke('settings:test', url),
    previewBackgroundAlpha: alpha => ipcRenderer.send('settings:preview-background-alpha', alpha),
    save: (url, nativeEffects, backgroundAlpha) =>
        ipcRenderer.invoke('settings:save', url, nativeEffects, backgroundAlpha),
    close: () => ipcRenderer.invoke('settings:close'),
});

let glassBackdrop = process.argv.includes('--solo-glass-backdrop');

function applyAppearance() {
    if (!document.documentElement) return;
    document.documentElement.classList.toggle('solo-settings--glass', glassBackdrop);
}

ipcRenderer.on('window:glass-backdrop', (_event, value) => {
    glassBackdrop = value === true;
    applyAppearance();
});

applyAppearance();
document.addEventListener('DOMContentLoaded', applyAppearance);
