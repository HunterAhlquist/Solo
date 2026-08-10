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

module.exports = { attachContextMenu };
