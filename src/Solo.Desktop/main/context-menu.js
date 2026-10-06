// Electron ships no default context menu, so without this a right-click does
// nothing at all — including on a misspelled word, where Chromium selects the
// word and then has nowhere to offer the corrections it already computed.
// Spelling suggestions come first, since that is most of what this menu is for;
// the ordinary editing actions follow, gated on what the click target can
// actually do so the menu never offers a dead Paste on a board background.
const { BrowserWindow, Menu } = require('electron');

function attachContextMenu(webContents) {
    webContents.on('context-menu', (_event, params) => {
        const template = buildTemplate(webContents, params);
        if (template.length === 0) {
            // Nothing selected, nothing editable, nothing misspelled: an empty
            // menu is worse than no menu.
            return;
        }

        Menu.buildFromTemplate(template).popup({
            window: BrowserWindow.fromWebContents(webContents) ?? undefined,
        });
    });
}

function buildTemplate(webContents, params) {
    const flags = params.editFlags;
    const hasSelection = params.selectionText.trim().length > 0;

    const sections = [
        spellingItems(webContents, params),
        params.isEditable
            ? [
                { role: 'undo', enabled: flags.canUndo },
                { role: 'redo', enabled: flags.canRedo },
            ]
            : [],
        clipboardItems(params, hasSelection),
        (params.isEditable || hasSelection) && flags.canSelectAll
            ? [{ role: 'selectAll' }]
            : [],
    ];

    return separated(sections.filter(items => items.length > 0));
}

// The corrections themselves. replaceMisspelling swaps the word Chromium has
// under the cursor, which is why this needs the webContents the click came
// from rather than whichever one happens to be focused when the item fires.
function spellingItems(webContents, params) {
    if (!params.misspelledWord) {
        return [];
    }

    const items = params.dictionarySuggestions.map(suggestion => ({
        label: suggestion,
        click: () => webContents.replaceMisspelling(suggestion),
    }));

    if (items.length === 0) {
        items.push({ label: 'No Guesses Found', enabled: false });
    }

    items.push({
        label: 'Add to Dictionary',
        click: () => webContents.session.addWordToSpellCheckerDictionary(params.misspelledWord),
    });

    return items;
}

function clipboardItems(params, hasSelection) {
    const items = [];
    const flags = params.editFlags;

    if (params.isEditable) {
        items.push({ role: 'cut', enabled: flags.canCut });
    }
    if (params.isEditable || hasSelection) {
        items.push({ role: 'copy', enabled: flags.canCopy });
    }
    if (params.isEditable) {
        items.push({ role: 'paste', enabled: flags.canPaste });
    }

    return items;
}

function separated(sections) {
    return sections.flatMap((items, index) =>
        index === 0 ? items : [{ type: 'separator' }, ...items]);
}

// ---------------------------------------------------------------------------
// The client's own menus (ISoloContextMenu): a page that handles a right-click
// itself prevents the browser's menu, so the handler above never sees it, and
// asks for one of these instead. The page says what's on it; main only draws
// it and reports which item was chosen, so a page can never reach a role or
// an action of the shell's through it.
// ---------------------------------------------------------------------------

const MAX_ITEMS = 32;
const MAX_LABEL = 200;

// Resolves with the chosen item's id, or null if the menu closes without one.
// Electron calls the popup's close callback before or after the item's click
// depending on the platform, so the close waits a beat before settling on
// "nothing chosen"; whichever settles first wins.
const CLICK_AFTER_CLOSE_MS = 100;

function popupClientMenu(webContents, items) {
    const template = clientTemplate(items);
    if (template.length === 0) {
        return Promise.resolve(null);
    }

    return new Promise(resolve => {
        const menu = Menu.buildFromTemplate(template.map(item => item.type === 'separator'
            ? item
            : { ...item, click: () => resolve(item.id) }));
        menu.popup({
            window: BrowserWindow.fromWebContents(webContents) ?? undefined,
            callback: () => setTimeout(() => resolve(null), CLICK_AFTER_CLOSE_MS),
        });
    });
}

// The renderer runs extension code, so what it sends is checked rather than
// trusted: plain labels and ids only, nothing that names a role or an
// accelerator, and a bound on how much of it there is.
function clientTemplate(items) {
    if (!Array.isArray(items)) {
        return [];
    }

    const template = [];
    for (const item of items.slice(0, MAX_ITEMS)) {
        if (typeof item?.id !== 'string' || typeof item.label !== 'string' || item.label.trim() === '') {
            continue;
        }

        if (item.separatorBefore === true && template.length > 0) {
            template.push({ type: 'separator' });
        }

        template.push({
            id: item.id,
            label: plainLabel(item.label.slice(0, MAX_LABEL)),
            enabled: item.enabled !== false,
        });
    }

    return template;
}

// Windows and Linux read "&" as a mnemonic marker and swallow it; doubled, it
// shows as itself. macOS shows labels as they are.
function plainLabel(label) {
    return process.platform === 'darwin' ? label : label.replaceAll('&', '&&');
}

module.exports = { attachContextMenu, popupClientMenu };
