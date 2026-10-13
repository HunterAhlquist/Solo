// The settings page's entire capability surface: list the instances, add one
// and see it through its first sign-in, open or remove one, and set the one
// preference. Nothing else crosses the bridge.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('soloSettings', {
    get: () => ipcRenderer.invoke('settings:get'),
    setNativeEffects: enabled => ipcRenderer.invoke('settings:native-effects', enabled),
    close: () => ipcRenderer.invoke('settings:close'),

    // Tests the address and saves the instance. Answers { ok, error } or
    // { ok, id, name, signIn }: with signIn the browser is now open and main
    // closes this window once the sign-in lands; without it main has already
    // opened the instance.
    add: url => ipcRenderer.invoke('instances:add', url),
    signIn: id => ipcRenderer.invoke('instances:sign-in', id),
    openNew: id => ipcRenderer.invoke('instances:open-new', id),
    open: id => ipcRenderer.invoke('instances:open', id),
    remove: id => ipcRenderer.invoke('instances:remove', id),

    // Instances are opened and closed from outside this window too.
    onInstancesChanged: listener =>
        ipcRenderer.on('instances:changed', (_event, instances) => listener(instances)),
    onSignInFailed: listener =>
        ipcRenderer.on('sign-in:failed', (_event, failure) => listener(failure)),
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
