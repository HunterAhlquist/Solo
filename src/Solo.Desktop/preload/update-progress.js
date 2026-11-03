// The update progress page's entire capability surface: hearing how far the
// download has got, and cancelling it.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('soloUpdate', {
    onState: listener => ipcRenderer.on('update-progress:state', (_event, state) => listener(state)),
    cancel: () => ipcRenderer.send('update-progress:cancel'),
});
