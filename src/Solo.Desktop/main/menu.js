// Standard menus plus the one entry Solo actually adds: Server Settings.
// Edit/View/Window are role-based — copy/paste shortcuts on macOS stop
// working without a real Edit menu, and reload/devtools stay useful in a
// tool like this.
const { Menu } = require('electron');

function buildMenu({ openSettingsWindow }) {
    const isMac = process.platform === 'darwin';

    const settingsItem = {
        label: 'Server Settings…',
        accelerator: 'CmdOrCtrl+,',
        click: () => openSettingsWindow(),
    };

    const template = [
        ...(isMac
            ? [{
                label: 'Solo',
                submenu: [
                    { role: 'about' },
                    { type: 'separator' },
                    settingsItem,
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
