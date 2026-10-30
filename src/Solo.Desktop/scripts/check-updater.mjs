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
import { mkdtempSync, writeFileSync, rmSync, readFileSync, existsSync, mkdirSync } from 'node:fs';
import os from 'node:os';
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

// Where each platform's packaged resources live, as electron-builder lays them out.
function resourcesDirFor(platform, appOutDir) {
    return platform === 'darwin'
        ? path.join(appOutDir, 'Solo.app', 'Contents', 'Resources')
        : path.join(appOutDir, 'resources');
}

// The slice of electron-builder's afterPack context that writeUpdateConfig reads.
function fakeContext(electronPlatformName, appOutDir, publish) {
    mkdirSync(appOutDir, { recursive: true });
    return {
        electronPlatformName,
        appOutDir,
        packager: {
            info: { config: { publish } },
            appInfo: { productFilename: 'Solo', updaterCacheDirName: 'solo-desktop-updater' },
        },
    };
}

// Two real resource directories rather than a mocked fs: one with the
// app-update.yml a target build writes, one without it, which is exactly what a
// `--dir` pack leaves behind.
const configured = mkdtempSync(path.join(os.tmpdir(), 'solo-updater-configured-'));
const bare = mkdtempSync(path.join(os.tmpdir(), 'solo-updater-bare-'));
writeFileSync(path.join(configured, 'app-update.yml'), 'provider: generic\n');

const platform = process.platform;
const resources = process.resourcesPath;
function as(
    target,
    { packaged = true, version = '1.18.0', appImage = undefined, updateConfig = true } = {},
) {
    Object.defineProperty(process, 'platform', { value: target, configurable: true });
    Object.defineProperty(process, 'resourcesPath', {
        value: updateConfig ? configured : bare,
        configurable: true,
    });
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

    // A `--dir` pack, or a .app copied out of dist/ rather than installed from
    // the installer: a real bundle with no app-update.yml in it. Without this
    // the user gets electron-updater's raw ENOENT on the missing path.
    for (const target of ['darwin', 'win32']) {
        const reason = as(target, { updateConfig: false });
        assert.match(reason, /no update configuration/, `${target} must explain a missing app-update.yml`);
        assert.doesNotMatch(reason, /ENOENT/);
    }
    assert.match(
        as('linux', { appImage: '/tmp/Solo.AppImage', updateConfig: false }),
        /no update configuration/);

    // The other half: the file has to be *there* in a real build. This
    // repository's mac flow packs with --dir and wraps with --prepackaged, so it
    // never reaches the target phase where electron-builder would write it —
    // which shipped apps that could not check for updates at all. afterPack
    // fills that gap, and these pin it.
    const afterPackModule = await import('./after-pack.mjs');
    const { writeUpdateConfig } = afterPackModule;
    const afterPack = afterPackModule.default;

    for (const platform of ['darwin', 'win32', 'linux']) {
        const out = mkdtempSync(path.join(os.tmpdir(), 'solo-afterpack-'));
        const context = fakeContext(platform, out, [{
            provider: 'generic',
            url: 'https://example.test/download/',
            channel: 'latest',
            useMultipleRangeRequest: false,
        }]);

        // Through the real hook on the platforms where it returns straight after
        // writing, so that deleting the call — not just breaking the function —
        // fails this check. darwin goes on to rewrite Mach-O launcher UUIDs,
        // which needs an actual bundle, so it gets the function directly.
        if (platform === 'darwin') {
            writeUpdateConfig(context);
        } else {
            await afterPack(context);
        }

        const written = path.join(resourcesDirFor(platform, out), 'app-update.yml');
        const body = readFileSync(written, 'utf8');
        // The exact keys electron-builder's own copy carries, which is what
        // electron-updater reads. updaterCacheDirName is the one that is derived
        // rather than copied, so a wrong value would be invisible until an
        // update tried to cache a download.
        assert.match(body, /^provider: generic$/m, `${platform} feed provider`);
        assert.match(body, /^url: https:\/\/example\.test\/download\/$/m, `${platform} feed url`);
        assert.match(body, /^channel: latest$/m);
        assert.match(body, /^useMultipleRangeRequest: false$/m);
        assert.match(body, /^updaterCacheDirName: solo-desktop-updater$/m);
        rmSync(out, { recursive: true, force: true });
    }

    // The hook is also driven directly by the other checks here, with only the
    // fields the behaviour under test needs. Reading the publish config off a
    // known shape threw a TypeError from inside afterPack and broke
    // check-mac-network-identity.mjs, which the release workflow requires — so
    // the exact context that check passes is pinned here too.
    const minimalOut = mkdtempSync(path.join(os.tmpdir(), 'solo-afterpack-minimal-'));
    await afterPack({
        electronPlatformName: 'win32',
        appOutDir: minimalOut,
        packager: { appInfo: { productFilename: 'Solo' } },
    });
    rmSync(minimalOut, { recursive: true, force: true });

    // A preview build has no publish config on purpose, so there is nothing to
    // write and nothing to mislead an updater with.
    const previewOut = mkdtempSync(path.join(os.tmpdir(), 'solo-afterpack-preview-'));
    await afterPack(fakeContext('win32', previewOut, null));
    assert.equal(
        existsSync(path.join(resourcesDirFor('win32', previewOut), 'app-update.yml')),
        false,
        'a preview build must get no update config');
    rmSync(previewOut, { recursive: true, force: true });

    console.log(
        'PASS: update checks are skipped exactly for development runs, preview builds, ' +
        'non-AppImage Linux and builds with no app-update.yml — and afterPack writes ' +
        'that file for every platform, which the --dir/--prepackaged mac flow depends on.');
} finally {
    Object.defineProperty(process, 'platform', { value: platform, configurable: true });
    Object.defineProperty(process, 'resourcesPath', { value: resources, configurable: true });
    rmSync(configured, { recursive: true, force: true });
    rmSync(bare, { recursive: true, force: true });
}
