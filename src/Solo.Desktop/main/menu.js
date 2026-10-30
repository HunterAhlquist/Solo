// Standard menus plus the entries Solo actually adds: Settings and the list of
// instances to open. Edit/View/Window are role-based — copy/paste shortcuts on
// macOS stop working without a real Edit menu, and reload/devtools stay useful
// in a tool like this.
//
// A menu is a snapshot: Electron has no way to grey out an item after the
// fact, so main rebuilds the whole thing whenever an instance is added,
// removed, opened or closed.
const { Menu } = require('electron');

// `instances` is [{ id, label, open }]. An instance that already has a window
// is listed but disabled: each gets one window at most, and the Window menu is
// what brings an open one forward.
function buildMenu({
    appName = 'Solo',
    openAboutWindow,
    openSettingsWindow,
    checkForUpdates,
    instances,
    openInstance,
}) {
    const isMac = process.platform === 'darwin';

    // Where each platform's users look for it: under the app menu next to About
    // on macOS, under Help on Windows and Linux. The item always exists — a
    // build that cannot update says so when clicked, which is more use than an
    // item that is missing for reasons the user cannot see.
    const updatesItem = {
        label: 'Check for Updates…',
        click: () => checkForUpdates?.(),
    };

    const settingsItem = {
        label: 'Settings…',
        accelerator: 'CmdOrCtrl+,',
        click: () => openSettingsWindow(),
    };

    const openInstanceItem = {
        label: 'Open Instance',
        submenu: instances.length > 0
            ? instances.map(instance => ({
                // The label is the instance's own display name, and a lone &
                // in a menu label marks a keyboard mnemonic instead of showing.
                label: instance.label.replace(/&/g, '&&'),
                enabled: !instance.open,
                click: () => openInstance(instance.id),
            }))
            : [{ label: 'No Instances', enabled: false }],
    };

    const template = [
        ...(isMac
            ? [{
                label: appName,
                submenu: [
                    // Not the `about` role: that is the native panel, and
                    // Solo's has a page in it (main/about.js).
                    { label: `About ${appName}`, click: () => openAboutWindow() },
                    updatesItem,
                    { type: 'separator' },
                    settingsItem,
                    openInstanceItem,
                    { type: 'separator' },
                    { role: 'hide' },
                    { role: 'hideOthers' },
                    { role: 'unhide' },
                    { type: 'separator' },
                    { role: 'quit' },
                ],
            }]
            : [{
                label: 'File',
                submenu: [
                    settingsItem,
                    openInstanceItem,
                    { type: 'separator' },
                    { role: 'quit' },
                ],
            }]),
        { role: 'editMenu' },
        {
            label: 'View',
            submenu: [
                { role: 'reload' },
                { role: 'forceReload' },
                { role: 'toggleDevTools' },
                { type: 'separator' },
                { role: 'resetZoom' },
                { role: 'zoomIn' },
                { role: 'zoomOut' },
                { type: 'separator' },
                { role: 'togglefullscreen' },
            ],
        },
        { role: 'windowMenu' },
        ...(isMac
            ? []
            : [{
                role: 'help',
                submenu: [
                    updatesItem,
                    { type: 'separator' },
                    { label: `About ${appName}`, click: () => openAboutWindow() },
                ],
            }]),
    ];

    return Menu.buildFromTemplate(template);
}

module.exports = { buildMenu };
