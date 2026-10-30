// The updater's decision about whether this build may check for updates at all.
//
// Each skip exists because electron-updater cannot work in that situation, and
// one of them is load-bearing in a way that is easy to undo: a preview build's
// publish config is null on purpose, because the feed points at the
// releases/latest redirect, which skips prereleases. A preview that checked
// would be told the stable line supersedes it and would replace itself with a
// different app — different appId, different settings, different user data. So
// that branch is pinned here rather than left to reading.
//
// electron is stubbed through the require cache: the real module only loads
// inside an Electron process, and spawning one to ask four questions about a
// version string is not worth the seconds.
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const desktopDir = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const require = createRequire(path.join(desktopDir, 'package.json'));

const app = { isPackaged: true, version: '1.18.0', getVersion: () => app.version, getName: () => 'Solo' };
require.cache[require.resolve('electron')] = {
    id: 'electron',
    filename: 'electron',
    loaded: true,
    exports: { app, dialog: {}, BrowserWindow: class {} },
};

const { unsupportedReason } = require(path.join(desktopDir, 'main', 'updater.js'));

const platform = process.platform;
function as(target, { packaged = true, version = '1.18.0', appImage = undefined } = {}) {
    Object.defineProperty(process, 'platform', { value: target, configurable: true });
    app.isPackaged = packaged;
    app.version = version;
    if (appImage === undefined) {
        delete process.env.APPIMAGE;
    } else {
        process.env.APPIMAGE = appImage;
    }

    return unsupportedReason();
}

try {
    // The one case that must work, on each platform that can update.
    assert.equal(as('darwin'), null, 'a packaged stable mac build must be able to check');
    assert.equal(as('win32'), null, 'a packaged stable Windows build must be able to check');
    assert.equal(as('linux', { appImage: '/tmp/Solo.AppImage' }), null,
        'a packaged stable AppImage must be able to check');

    // A development run has no app-update.yml to read.
    assert.match(as('darwin', { packaged: false }), /installed build/);

    // The branch that stops a preview replacing itself with the stable app.
    for (const version of ['1.18.0-rc.1', '1.18.0-design-docs.1', '1.19.0-preview.2']) {
        assert.match(as('darwin', { version }), /preview build/,
            `${version} must be recognised as a preview and skipped`);
    }

    // A stable version must not be mistaken for a preview, or no one ever updates.
    for (const version of ['1.18.0', '2.0.0', '1.18.1']) {
        assert.equal(as('darwin', { version }), null, `${version} must not read as a preview`);
    }

    // electron-updater can only replace an AppImage in place; a .deb or a
    // distro package is the package manager's business.
    assert.match(as('linux'), /AppImage/);

    console.log('PASS: update checks are skipped exactly for development runs, preview builds and non-AppImage Linux.');
} finally {
    Object.defineProperty(process, 'platform', { value: platform, configurable: true });
}
