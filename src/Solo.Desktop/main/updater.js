// In-app updates, through electron-updater against the generic feed baked into
// the build at packaging time (electron-builder.config.cjs writes
// app-update.yml). docs/releases.md covers the feed itself; this file is only
// the decision of when to check and what to say.
//
// autoDownload is off. An installer here is well over a hundred megabytes, and
// spending that on someone's connection before they have agreed to it is not a
// reasonable default for a desktop app that is otherwise idle. autoInstallOnAppQuit
// is left on, so an update that *was* downloaded applies at the next quit
// whether or not the user took the "Restart now" offer.
//
// Three situations silently skip the check, each because electron-updater
// cannot work there and would only produce an error dialog nobody can act on:
//
//   * an unpackaged development run, which has no app-update.yml;
//   * a preview build, whose publish config is null on purpose — the
//     releases/latest redirect the feed points at skips prereleases, so a
//     preview would be told to "update" to the stable line and replace itself;
//   * Linux outside an AppImage, the only Linux target electron-updater can
//     replace in place.
//
// A manual check reports those out loud instead, because a user who picked
// "Check for Updates…" is owed an answer.
const { app, dialog } = require('electron');
const fs = require('fs');
const path = require('path');
const { identityForVersion } = require('./app-identity');

let autoUpdater = null;
let state = 'idle';
let manual = false;
let wired = false;

// Why this build cannot check, or null when it can.
function unsupportedReason() {
    if (!app.isPackaged) {
        return 'Updates are only available in an installed build.';
    }

    if (identityForVersion(app.getVersion()).preview) {
        return 'This is a preview build. Preview releases are installed by hand — ' +
            'see the releases page on GitHub.';
    }

    if (process.platform === 'linux' && !process.env.APPIMAGE) {
        return 'In-app updates on Linux are only supported for the AppImage build.';
    }

    // electron-builder writes app-update.yml per *target*, so a `--dir` pack
    // produces a perfectly good .app with no update config in it. Copying that
    // .app out of dist/ instead of installing from the dmg is the normal way to
    // test a local build, and without this check electron-updater reads the
    // missing file and the user gets a raw ENOENT path in a dialog.
    //
    // Checked rather than inferred from the publish config, because this file
    // cannot see that config — and because the same absence covers a fork that
    // builds without one.
    if (!fs.existsSync(path.join(process.resourcesPath, 'app-update.yml'))) {
        return 'This build carries no update configuration, so it cannot check for updates. ' +
            'That is what an app packed with --dir looks like: install from the .dmg, .exe or ' +
            'AppImage rather than running the .app copied out of dist/.';
    }

    return null;
}

function updater() {
    if (autoUpdater) {
        return autoUpdater;
    }

    // Required late: electron-updater reads the packaged app's update config as
    // it loads, so requiring it in a development run logs a warning for a
    // feature that run cannot use.
    ({ autoUpdater } = require('electron-updater'));
    autoUpdater.autoDownload = false;
    autoUpdater.autoInstallOnAppQuit = true;
    autoUpdater.logger = null;
    return autoUpdater;
}

// `getWindow` returns the window to parent dialogs on, or null. Dialogs are
// parented so they are sheets on macOS rather than free-floating panels.
function registerUpdater({ getWindow }) {
    const parent = () => {
        const win = getWindow?.();
        return win && !win.isDestroyed() ? win : null;
    };

    const show = options => {
        const win = parent();
        return win ? dialog.showMessageBox(win, options) : dialog.showMessageBox(options);
    };

    // Nothing to wire when this build cannot update, and asking for the
    // handlers would load electron-updater to do it — which reads the packaged
    // app's update config as it loads and complains in a development run. The
    // menu item still works: checkForUpdates explains the reason itself.
    if (wired || unsupportedReason()) {
        return;
    }
    wired = true;
    const api = updater();

    api.on('error', error => {
        state = 'idle';
        setProgress(parent(), -1);
        if (!manual) {
            // An automatic check that fails is not news: the next launch tries
            // again, and the alternative is a dialog on startup about a network
            // that was briefly down.
            console.warn('Update check failed:', error?.message ?? error);
            return;
        }

        manual = false;
        show({
            type: 'error',
            message: 'Couldn’t check for updates',
            detail: String(error?.message ?? error),
            buttons: ['OK'],
        });
    });

    api.on('update-not-available', () => {
        state = 'idle';
        if (!manual) {
            return;
        }

        manual = false;
        show({
            type: 'info',
            message: `${app.getName()} ${app.getVersion()} is up to date.`,
            buttons: ['OK'],
        });
    });

    api.on('update-available', async info => {
        manual = false;
        // The download is the user's call, so the dialog states the version and
        // waits. Declining leaves nothing downloaded and nothing scheduled.
        const { response } = await show({
            type: 'info',
            message: `${app.getName()} ${info.version} is available.`,
            detail: `You have ${app.getVersion()}. Downloading is about ` +
                `${describeSize(info.files)}; the update installs when you quit.`,
            buttons: ['Download', 'Not Now'],
            defaultId: 0,
            cancelId: 1,
        });

        if (response !== 0) {
            state = 'idle';
            return;
        }

        state = 'downloading';
        try {
            await api.downloadUpdate();
        } catch (error) {
            // Surfaced here as well as on the error event: a rejection from
            // downloadUpdate is the one failure the user explicitly asked for
            // and is waiting on.
            state = 'idle';
            setProgress(parent(), -1);
            show({
                type: 'error',
                message: 'The update couldn’t be downloaded',
                detail: String(error?.message ?? error),
                buttons: ['OK'],
            });
        }
    });

    api.on('download-progress', progress => {
        // The only progress affordance that needs no window of its own: the
        // dock icon on macOS, the taskbar button on Windows.
        setProgress(parent(), (progress?.percent ?? 0) / 100);
    });

    api.on('update-downloaded', async info => {
        state = 'downloaded';
        setProgress(parent(), -1);

        const { response } = await show({
            type: 'info',
            message: `${app.getName()} ${info.version} is ready to install.`,
            detail: 'Restarting finishes the update now. Otherwise it installs the ' +
                'next time you quit.',
            buttons: ['Restart Now', 'Later'],
            defaultId: 0,
            cancelId: 1,
        });

        if (response === 0) {
            // isSilent false, isForceRunAfter true: the installer shows itself
            // and the app comes back up.
            api.quitAndInstall(false, true);
        }
    });
}

// `fromMenu` distinguishes "the user asked" from the check on startup, which
// decides whether a negative answer is reported.
async function checkForUpdates({ fromMenu = false, getWindow } = {}) {
    const reason = unsupportedReason();
    if (reason) {
        if (fromMenu) {
            const win = getWindow?.();
            const options = {
                type: 'info',
                message: 'Updates aren’t available for this build.',
                detail: reason,
                buttons: ['OK'],
            };
            await (win && !win.isDestroyed()
                ? dialog.showMessageBox(win, options)
                : dialog.showMessageBox(options));
        }
        return;
    }

    // Already busy: a second check would stack a second set of dialogs on the
    // first one's events.
    if (state === 'downloading' || state === 'checking') {
        return;
    }

    if (state === 'downloaded') {
        if (fromMenu) {
            const win = getWindow?.();
            const options = {
                type: 'info',
                message: 'An update is already downloaded.',
                detail: 'It installs the next time you quit.',
                buttons: ['OK'],
            };
            await (win && !win.isDestroyed()
                ? dialog.showMessageBox(win, options)
                : dialog.showMessageBox(options));
        }
        return;
    }

    manual = fromMenu;
    state = 'checking';
    try {
        await updater().checkForUpdates();
    } catch (error) {
        // The error event has already reported this; swallow the rejection so
        // it does not surface as an unhandled one.
        state = 'idle';
        console.warn('Update check failed:', error?.message ?? error);
    }
}

function setProgress(win, value) {
    if (win && !win.isDestroyed()) {
        win.setProgressBar(value);
    }
}

// electron-updater reports the files it would fetch; the primary one is what
// actually downloads. Falls back to saying nothing rather than guessing.
function describeSize(files) {
    const size = Array.isArray(files) ? files.find(file => file?.size > 0)?.size : null;
    if (!size) {
        return 'a few hundred megabytes';
    }

    return `${Math.round(size / 1024 / 1024)} MB`;
}

module.exports = { registerUpdater, checkForUpdates, unsupportedReason };
