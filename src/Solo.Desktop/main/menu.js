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
function buildMenu({ openSettingsWindow, instances, openInstance }) {
    const isMac = process.platform === 'darwin';

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
                label: 'Solo',
                submenu: [
                    { role: 'about' },
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
    ];

    return Menu.buildFromTemplate(template);
}

module.exports = { buildMenu };
