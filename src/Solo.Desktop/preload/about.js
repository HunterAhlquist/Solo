// The About page's entire capability surface: asking for the window to fit its
// content, which changes size when the logo turns into the game.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('soloAbout', {
    fit: height => ipcRenderer.send('about:fit', height),
});
