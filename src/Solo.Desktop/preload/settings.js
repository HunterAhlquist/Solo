// The settings page's entire capability surface: read the current settings,
// test a candidate URL, save it, or close the window. Nothing else crosses
// the bridge.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('soloSettings', {
    get: () => ipcRenderer.invoke('settings:get'),
    test: url => ipcRenderer.invoke('settings:test', url),
    save: url => ipcRenderer.invoke('settings:save', url),
    close: () => ipcRenderer.invoke('settings:close'),
});
