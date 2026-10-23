// Solo desktop shell. The Blazor client is served from a local origin
// (http://localhost:{port}) and all /api + /hubs traffic is proxied to a Solo
// instance added in the settings window — the client itself is the untouched
// web build. Every instance has a local origin of its own and at most one
// window. Sign-in happens in the system browser on the instance's own origin
// (main/desktop-auth.js).
const { app, BrowserWindow, Menu, dialog, ipcMain, nativeTheme, net, screen, session, shell } = require('electron');
const path = require('path');
const { configureAppIdentity } = require('./main/app-identity');
configureAppIdentity(app, process.env);
const settingsStore = require('./main/settings');
const { connectionError } = require('./main/connection-error');
const { createSoloServer } = require('./main/server');
const { buildMenu } = require('./main/menu');
const { attachContextMenu, popupClientMenu } = require('./main/context-menu');
const { DesktopAuth } = require('./main/desktop-auth');
const { presentAuthSheet } = require('./main/auth-sheet');
const { openAboutWindow, registerAboutIpc } = require('./main/about');

let settings = null;
let settingsWindow = null;
// undefined until a window first wants it, null once it is known not to work.
let liquidGlass;

// Every open instance window and what it is showing, least recently focused
// first: focusing a window moves it to the end, so the last entry is the
// "current" window an instance can be opened into.
//   BrowserWindow → { instance, pageTitle, themeSource, reconnectPending }
const windows = new Map();

// The local servers, by instance id. One is started the first time its
// instance is opened and then left running: it is an idle listener, and
// keeping it means a window can be reopened without the port being contended.
//   instance id → { server, listening: Promise }
const servers = new Map();

// Browser sign-ins, by instance id. One DesktopAuth holds one pending flow, so
// one each keeps a sign-in started for one instance from cancelling another's.
const signIns = new Map();

// The instance whose first sign-in the settings window is waiting on, if any.
let settingsSignInId = null;
let addingInstance = false;

process.on('uncaughtException', handleUncaughtException);

// Google's sign-in pages refuse "embedded" browsers by user agent. Dropping
// the Electron and app-name tokens leaves a plain Chrome UA, which Google
// accepts. This must happen before any window is created.
app.userAgentFallback = app.userAgentFallback
    .replace(new RegExp(`\\s${app.getName().replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}/[\\S]+`, 'i'), '')
    .replace(/\sElectron\/[\S]+/, '');

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
    app.quit();
} else {
    app.on('second-instance', () => {
        const win = currentWindow() ?? settingsWindow;
        if (win) {
            focusWindow(win);
        }
    });

    app.whenReady().then(onReady);
}

function onReady() {
    settings = settingsStore.load();
    registerIpc();
    refreshInstanceViews();

    const instance = findInstance(settings.lastInstanceId) ?? settings.instances[0];
    if (instance) {
        openInstanceWindow(instance);
    } else {
        // First run: no instance to talk to yet, so settings is the app.
        openSettingsWindow();
    }
}

function handleUncaughtException(error) {
    if (!isConnectionReset(error)) {
        // Keep Electron's normal crash behavior for programming errors. The
        // next throw runs without this handler, so an unrelated exception is
        // never mistaken for a recoverable network transition.
        process.removeListener('uncaughtException', handleUncaughtException);
        setImmediate(() => { throw error; });
        return;
    }

    if (![...windows.values()].some(state => state.reconnectPending)) {
        console.warn('Solo lost its network connection; reconnecting when the window becomes active.');
    }

    // The reset surfaces process-wide, with nothing to say whose socket it
    // was — and what causes it (sleep, a network change) cuts every instance's
    // connections alike. So each window reconnects, the next time it is used.
    for (const [win, state] of windows) {
        state.reconnectPending = true;
        if (win.isFocused?.()) {
            setImmediate(() => recoverNetworkConnection(win));
        }
    }
}

function isConnectionReset(error) {
    return error?.code === 'ECONNRESET' || error?.message === 'read ECONNRESET';
}

function recoverNetworkConnection(win) {
    const state = windows.get(win);
    if (!state?.reconnectPending) {
        return;
    }

    const contents = win.webContents;
    if (!contents || contents.isDestroyed()) {
        return;
    }

    state.reconnectPending = false;
    try {
        // A fresh Blazor boot re-establishes both ordinary API traffic and the
        // SignalR connection after Chromium resumes on a different network.
        contents.reload();
    } catch (error) {
        state.reconnectPending = true;
        console.warn(`Solo could not reconnect yet: ${error.message}`);
    }
}

// Deliberately no macOS lives-in-the-dock exception: Solo's windows are the
// app, and closing the last one should mean a closed app on every platform.
app.on('window-all-closed', () => app.quit());

// ---------------------------------------------------------------------------
// Instances and their windows
// ---------------------------------------------------------------------------

function findInstance(id) {
    return settings.instances.find(instance => instance.id === id) ?? null;
}

function windowFor(instance) {
    for (const [win, state] of windows) {
        if (state.instance === instance) {
            return win;
        }
    }
    return null;
}

function currentWindow() {
    return [...windows.keys()].at(-1) ?? null;
}

function focusWindow(win) {
    if (win.isMinimized()) win.restore();
    win.show();
    win.focus();
}

// What the menu and the settings page list.
function instanceList() {
    return settings.instances.map(instance => ({
        id: instance.id,
        name: instance.name,
        // Two instances can go by one display name — production and staging,
        // say — and a menu of identical items is no menu at all.
        label: settings.instances.some(other => other !== instance && other.name === instance.name)
            ? `${instance.name} (${settingsStore.instanceAddress(instance.url)})`
            : instance.name,
        url: instance.url,
        open: windowFor(instance) !== null,
    }));
}

// Everything that shows which instances exist and which are open. The menu is
// rebuilt rather than patched: Electron cannot disable an item once built.
function refreshInstanceViews() {
    Menu.setApplicationMenu(buildMenu({
        appName: app.getName(),
        openAboutWindow: () => openAboutWindow(HARDENED_WEB_PREFERENCES),
        openSettingsWindow,
        instances: instanceList(),
        openInstance: requestOpenInstance,
    }));
    for (const win of windows.keys()) {
        applyTitle(win);
    }
    sendToSettings('instances:changed', instanceList());
}

function sendToSettings(channel, payload) {
    if (settingsWindow && !settingsWindow.isDestroyed()) {
        settingsWindow.webContents.send(channel, payload);
    }
}

// With the title bar hidden, the title is what the Window menu, the taskbar
// and Mission Control show. Once there is more than one instance it has to say
// which one a window is, or two boards with the same name look identical. The
// client ends its titles with the instance's display name where it has one,
// so only a title that doesn't already say gets the name added.
function applyTitle(win) {
    const { pageTitle, instance } = windows.get(win);
    win.setTitle(settings.instances.length > 1 && !pageTitle.includes(instance.name)
        ? `${pageTitle} — ${instance.name}`
        : pageTitle);
}

// Asks the instance what it is called now. The name is its operator's to
// change (Admin settings), and the client reloads when they do — so asking on
// every page load keeps the menu in step without a channel of its own. An
// instance that doesn't answer keeps the name it had.
async function refreshInstanceName(instance) {
    const answer = await fetchInstanceConfig(instance.url);
    if (!answer.ok || !settings.instances.includes(instance)) {
        return;
    }

    const name = settingsStore.instanceName(instance.url, answer.config.displayName);
    if (name !== instance.name) {
        instance.name = name;
        settingsStore.save(settings);
        refreshInstanceViews();
    }
}

function rememberLastInstance(instance) {
    if (settings.lastInstanceId !== instance.id) {
        settings.lastInstanceId = instance.id;
        settingsStore.save(settings);
    }
}

// Opens the instance in a window of its own, or brings forward the one it
// already has. Resolves with the window, or null if there is none to show.
async function openInstanceWindow(instance) {
    if (!windowFor(instance) && !await startServer(instance)) {
        return null;
    }

    // Starting the server was awaited, so look again: the instance may have
    // been opened by a second request, or removed, in the meantime.
    if (!settings.instances.includes(instance)) {
        return null;
    }

    const existing = windowFor(instance);
    if (existing) {
        focusWindow(existing);
        return existing;
    }
    return createInstanceWindow(instance);
}

// Points an open window at a different instance. The window stays — size,
// position, fullscreen — and only what it shows changes; the instance it was
// showing is simply no longer open.
async function showInstanceInWindow(win, instance) {
    if (!await startServer(instance)) {
        return;
    }

    const state = windows.get(win);
    if (!state || windowFor(instance) || !settings.instances.includes(instance)) {
        return;
    }

    // The binding first: everything that asks which instance this window is
    // (navigation checks, the bridge, a session delivery) reads it, and must
    // already see the new answer when the new page starts loading.
    state.instance = instance;
    state.reconnectPending = false;
    win.loadURL(localUrl(instance));
    focusWindow(win);
    rememberLastInstance(instance);
    refreshInstanceViews();
}

// The menu's and the settings page's "open": an instance that is already open
// comes forward, and otherwise the user chooses between a window of its own
// and taking over the current one.
async function requestOpenInstance(id) {
    const instance = findInstance(id);
    if (!instance) {
        return;
    }

    const current = currentWindow();
    if (windowFor(instance) || !current) {
        await openInstanceWindow(instance);
        return;
    }

    // Asked on whichever window is in front — the menu is reachable from the
    // settings window too — and naming the current one, since it may not be.
    const { response } = await dialog.showMessageBox(BrowserWindow.getFocusedWindow() ?? current, {
        type: 'question',
        buttons: ['New Window', 'Current Window', 'Cancel'],
        defaultId: 0,
        cancelId: 2,
        message: `Open ${instance.name}`,
        detail: 'Open it in a new window, or in the current window in place of ' +
            `${windows.get(current).instance.name}?`,
    });
    if (response === 2) {
        return;
    }

    // The dialog was awaited: the window it was asked about may be gone.
    if (response === 1 && windows.has(current) && !windowFor(instance)) {
        await showInstanceInWindow(current, instance);
    } else {
        await openInstanceWindow(instance);
    }
}

// ---------------------------------------------------------------------------
// Local servers
// ---------------------------------------------------------------------------

function clientDir() {
    return process.env.SOLO_CLIENT_DIR ?? path.join(__dirname, 'wwwroot');
}

// Starts the instance's server if it isn't running, and says so when it can't.
async function startServer(instance) {
    try {
        await ensureServer(instance);
        return true;
    } catch (err) {
        dialog.showErrorBox(
            'Solo could not start its local server',
            `Port ${instance.port} is unavailable (${err.message}).\n` +
            `Change the port for ${instance.name} in settings.json and restart.`);
        if (windows.size === 0 && !settingsWindow) {
            app.quit();
        }
        return false;
    }
}

function ensureServer(instance) {
    let entry = servers.get(instance.id);
    if (!entry) {
        const server = createSoloServer({
            clientDir: clientDir(),
            getTarget: () => instance.url,
        });
        const listening = new Promise((resolve, reject) => {
            server.once('error', reject);
            server.listen(instance.port, '127.0.0.1', () => {
                server.removeListener('error', reject);
                resolve();
            });
        });
        // The promise is shared by everyone opening the instance at once; a
        // failed start is forgotten before they hear of it, so a retry starts
        // over instead of awaiting the same rejection forever.
        entry = {
            server,
            listening: listening.catch(err => {
                servers.delete(instance.id);
                throw err;
            }),
        };
        servers.set(instance.id, entry);
    }
    return entry.listening;
}

function stopServer(instance) {
    const entry = servers.get(instance.id);
    if (!entry) {
        return;
    }

    servers.delete(instance.id);
    entry.server.close();
    entry.server.closeAllConnections?.();
}

// Gives a new instance the first local port that is neither another
// instance's nor taken by something else on the machine, by starting its
// server there — the only honest test of whether a port can be had.
async function claimPort(instance) {
    const taken = new Set(settings.instances.map(other => other.port));
    for (let port = settings.port; port < Math.min(settings.port + 100, 65536); port++) {
        if (taken.has(port)) {
            continue;
        }

        instance.port = port;
        try {
            await ensureServer(instance);
            return;
        } catch (err) {
            if (err.code !== 'EADDRINUSE') {
                throw err;
            }
        }
    }
    throw new Error(`no free local port from ${settings.port} up`);
}

function localOrigin(instance) {
    // localhost rather than 127.0.0.1: for non-Google OIDC providers this is
    // still the redirect origin operators register, so it must be stable and
    // predictable. (Google sign-in runs on the instance's own origin.)
    return `http://localhost:${instance.port}`;
}

function localUrl(instance) {
    return `${localOrigin(instance)}/`;
}

// Forgets everything the client stored under the instance's origin — above
// all its session. Done when an instance is removed, and again before a port
// is first used: a port is reused once its instance is gone, and a session
// left behind there would be sent, as a bearer token, to whichever server the
// next instance on that port turned out to be.
async function clearLocalStorage(instance) {
    try {
        await session.defaultSession.clearStorageData({ origin: localOrigin(instance) });
    } catch (err) {
        console.warn(`Solo could not clear what was stored for ${instance.name}: ${err.message}`);
    }
}

const isMac = process.platform === 'darwin';

function nativeEffectsEnabled() {
    return isMac && settings?.nativeEffects !== false &&
        process.env.SOLO_DISABLE_LIQUID_GLASS !== '1';
}

// The native package is darwin-only while Solo also installs and packages on
// Windows and Linux, so it is an optional dependency and never loaded there.
// macOS uses it when possible and falls back to Electron's AppKit vibrancy
// when this optional native layer is unavailable.
function glassModule() {
    if (liquidGlass === undefined) {
        liquidGlass = loadLiquidGlass();
    }
    return liquidGlass;
}

function loadLiquidGlass() {
    if (!isMac) {
        return null;
    }

    try {
        const imported = require('electron-liquid-glass');
        const candidate = imported.default ?? imported;
        if (typeof candidate.addView !== 'function' ||
            (typeof candidate.isGlassSupported === 'function' && !candidate.isGlassSupported())) {
            console.warn('Solo Liquid Glass is unavailable on this version of macOS; using Electron vibrancy.');
            return null;
        }

        return candidate;
    } catch (err) {
        // A missing, mismatched, or unsigned optional native binary should not
        // cost the app its translucent desktop treatment.
        console.error('Solo Liquid Glass could not be loaded; using Electron vibrancy.', err);
        return null;
    }
}

// Electron's current defaults already, spelled out so a future default change
// — or a stray webPreferences edit — can't quietly hand a renderer Node.
// The renderer runs a Blazor app that renders other people's markdown; it has
// no business reaching past the context bridge in preload/.
const HARDENED_WEB_PREFERENCES = {
    contextIsolation: true,
    nodeIntegration: false,
    nodeIntegrationInSubFrames: false,
    sandbox: true,
    webSecurity: true,
    allowRunningInsecureContent: false,
    webviewTag: false,
};

// The client's own app bar height (MudBlazor's --mud-appbar-height, 5em against
// the theme's 0.875rem base). The window controls are drawn inside it, so both
// sides have to agree on the number: see the "Desktop shell chrome" block in
// Solo.Client/wwwroot/css/app.css.
const TITLE_BAR_HEIGHT = 70;

function createInstanceWindow(instance) {
    const useGlassBackdrop = nativeEffectsEnabled();
    const windowGlass = useGlassBackdrop ? glassModule() : null;
    const useLiquidGlass = windowGlass !== null;
    const useVibrancyFallback = useGlassBackdrop && !useLiquidGlass;

    const win = new BrowserWindow({
        width: 1440,
        height: 920,
        minWidth: 900,
        minHeight: 600,
        show: false,
        backgroundColor: useGlassBackdrop ? '#00000000' : '#000000',
        transparent: useGlassBackdrop,
        ...(useVibrancyFallback
            ? { vibrancy: 'under-window', visualEffectState: 'active' }
            : {}),
        title: 'Solo',
        // No OS title bar: the platform's own window controls sit on the app
        // bar instead, the way most Electron apps ship. macOS keeps its inset
        // traffic lights on the left; Windows and Linux get the Window Controls
        // Overlay on the right, whose geometry the renderer reads back through
        // the env(titlebar-area-*) variables. The app bar itself is the drag
        // region — the client marks it, keyed off the classes the preload sets.
        titleBarStyle: isMac ? 'hiddenInset' : 'hidden',
        ...(isMac
            ? { trafficLightPosition: { x: 18, y: Math.round((TITLE_BAR_HEIGHT - 14) / 2) } }
            : {
                titleBarOverlay: {
                    color: '#000000',
                    symbolColor: '#fff600',
                    height: TITLE_BAR_HEIGHT,
                },
                // Without a title bar the menu bar would drop into the client
                // area and sit on top of the app bar. Alt still summons it.
                autoHideMenuBar: true,
            }),
        webPreferences: {
            preload: path.join(__dirname, 'preload', 'desktop.js'),
            // Both as arguments so the first paint is already right; later
            // changes to the opacity arrive over IPC (sendBackgroundAlpha).
            ...(useGlassBackdrop
                ? {
                    additionalArguments: [
                        '--solo-glass-backdrop',
                        `--solo-glass-background-alpha=${settings.backgroundAlpha}`,
                    ],
                }
                : {}),
            ...HARDENED_WEB_PREFERENCES,
        },
    });

    // Registered before anything can ask about it, as the newest and so the
    // current window. The page title is the client's until it says otherwise.
    windows.set(win, {
        instance,
        pageTitle: 'Solo',
        themeSource: null,
        reconnectPending: false,
    });

    if (useGlassBackdrop) {
        win.setWindowButtonVisibility(true);
    }

    if (useLiquidGlass) {
        // The package adds one native NSGlassEffectView beneath Chromium. The
        // renderer class supplied above makes only the intended CSS surfaces
        // translucent; Blazor never needs access to the native module.
        win.webContents.once('did-finish-load', () => {
            try {
                const viewId = windowGlass.addView(win.getNativeWindowHandle());
                if (viewId < 0) {
                    throw new Error('the native add-on returned no view');
                }

                win.webContents.send('window:glass-backdrop', true);
                console.log(`Solo Liquid Glass enabled (view ${viewId}).`);
            } catch (err) {
                liquidGlass = null;
                console.error('Solo Liquid Glass could not be applied; using Electron vibrancy.', err);
                try {
                    win.setVibrancy('under-window');
                    win.webContents.send('window:glass-backdrop', true);
                    console.log('Solo glass fallback enabled (Electron vibrancy).');
                } catch (fallbackError) {
                    win.webContents.send('window:glass-backdrop', false);
                    console.error('Solo could not apply its glass fallback; using opaque renderer surfaces.', fallbackError);
                }
            }
        });
    } else if (useVibrancyFallback) {
        console.log('Solo glass fallback enabled (Electron vibrancy).');
    }

    win.once('ready-to-show', () => win.show());
    win.on('focus', () => onWindowFocused(win));
    win.on('closed', () => {
        windows.delete(win);
        refreshInstanceViews();
    });
    win.webContents.on('did-finish-load', () => {
        // Whichever instance the window shows by now; it can be pointed at
        // another one.
        const shown = windows.get(win)?.instance;
        if (shown) {
            refreshInstanceName(shown);
        }
    });
    win.on('page-title-updated', (event, title) => {
        event.preventDefault();
        windows.get(win).pageTitle = title;
        applyTitle(win);
    });

    // Spelling corrections and copy/paste on right-click; Electron has no
    // context menu of its own.
    attachContextMenu(win.webContents);

    // The window is the app; it never navigates off its own origin. Task
    // descriptions and comments are user-written markdown rendered in this
    // renderer, so a link in one is untrusted input — it goes to the system
    // browser like any other external link, and never becomes a navigation
    // here.
    win.webContents.on('will-navigate', (event, url) => {
        if (!isLocalUrl(url, windows.get(win).instance)) {
            event.preventDefault();
            openExternally(url);
        }
    });

    // Fullscreen takes the traffic lights away, so the app bar must stop
    // reserving room for them. A reload asks for the state instead (below).
    for (const [event, fullScreen] of [['enter-full-screen', true], ['leave-full-screen', false]]) {
        win.on(event, () => win.webContents.send('window:full-screen', fullScreen));
    }

    // Chromium does not expose AppKit rotation as Safari's DOM gesture events.
    // Keep the native event generic so any canvas can opt in through preload.
    if (isMac) {
        win.on('rotate-gesture', (_event, rotation) => {
            if (!Number.isFinite(rotation) || win.webContents.isDestroyed()) return;
            const cursor = screen.getCursorScreenPoint();
            const bounds = win.getContentBounds();
            const scale = win.webContents.getZoomFactor();
            win.webContents.send('window:rotate-gesture', {
                rotation,
                clientX: (cursor.x - bounds.x) / scale,
                clientY: (cursor.y - bounds.y) / scale,
            });
        });
    }

    // The client's "Authorize in browser" button starts sign-in over IPC, but
    // if Google's widget ever runs in-app anyway, its popup can never succeed
    // (Google blocks sign-in inside Electron outright) — reroute it into the
    // same browser flow. Every other link belongs in the system browser too.
    win.webContents.setWindowOpenHandler(({ url }) => {
        if (hostnameOf(url) === 'accounts.google.com') {
            beginBrowserSignIn(windows.get(win).instance);
        } else {
            openExternally(url);
        }
        return { action: 'deny' };
    });

    win.loadURL(localUrl(instance));
    rememberLastInstance(instance);
    refreshInstanceViews();
    return win;
}

function onWindowFocused(win) {
    const state = windows.get(win);
    if (!state) {
        return;
    }

    // Re-inserting moves it to the end of the map: the current window.
    windows.delete(win);
    windows.set(win, state);
    rememberLastInstance(state.instance);

    // Electron has one native appearance for the whole app, while each
    // instance's client holds a theme preference of its own. The window in
    // use decides, so the one being looked at is always right.
    if (state.themeSource) {
        nativeTheme.themeSource = state.themeSource;
    }

    recoverNetworkConnection(win);
}

// Hands a URL to the OS. Only ever http(s): openExternal asks the desktop to
// act on whatever scheme it is given, and the URLs reaching here come from
// markdown somebody else wrote — a file:, smb: or custom-scheme link would be
// asking the OS to open a share or launch a handler on their say-so.
function openExternally(url) {
    try {
        const { protocol } = new URL(url);
        if (protocol === 'http:' || protocol === 'https:') {
            shell.openExternal(url);
        }
    } catch {
        // Not a URL at all; nothing to open.
    }
}

function hostnameOf(url) {
    try {
        return new URL(url).hostname;
    } catch {
        return null;
    }
}

// The instance's own local origin — its local server, which is the only thing
// a window showing that instance is ever allowed to be showing.
function isLocalUrl(url, instance) {
    try {
        const { protocol, hostname, port } = new URL(url);
        return protocol === 'http:' &&
            (hostname === 'localhost' || hostname === '127.0.0.1') &&
            port === String(instance.port);
    } catch {
        return false;
    }
}

function beginBrowserSignIn(instance) {
    let auth = signIns.get(instance.id);
    if (!auth) {
        auth = new DesktopAuth({ appName: app.getName() });
        signIns.set(instance.id, auth);
    }

    return auth.begin({
        instanceUrl: instance.url,
        onSession: session => deliverSession(instance, session),
        onFailure: message => notifySignInFailed(instance, message),
        // Over the window the sign-in started from: settings while adding
        // the instance, the instance's own window otherwise.
        sheet: (url, onDismissed) => presentAuthSheet(url,
            settingsSignInId === instance.id ? settingsWindow : windowFor(instance),
            onDismissed),
    });
}

// Hands a browser-won Solo session to the instance's client via
// `solo.desktop.deliverSession` (wwwroot/js/solo-interop.js): the instance
// verified the sign-in and minted the session server-side, so the client
// only stores it and refreshes — the desktop app then stays signed in on
// the session's sliding expiry, no hourly Google credential involved.
//
// The instance need not have a window yet. One added in settings is signed in
// to first and only then opened, so this opens the window it delivers to.
async function deliverSession(instance, session) {
    const firstSignIn = settingsSignInId === instance.id;
    const win = await openInstanceWindow(instance);
    if (!win) {
        return;
    }

    if (firstSignIn) {
        // Only now that the instance has its window: with quit-on-all-closed,
        // closing settings first could briefly leave zero windows and end the
        // app.
        settingsSignInId = null;
        settingsWindow?.close();
    }

    const contents = win.webContents;
    if (contents.isLoading()) {
        await new Promise(resolve => contents.once('did-finish-load', resolve));
    }

    // The window can be pointed at another instance while this waits. The
    // origin is checked again by the page itself, because this is the one
    // mistake that must not happen: a session stored under the wrong origin
    // is a credential the client would send to the wrong server.
    if (windows.get(win)?.instance !== instance) {
        return;
    }

    let outcome;
    try {
        outcome = await contents.executeJavaScript(
            `location.origin !== ${JSON.stringify(localOrigin(instance))}
                ? 'wrong-origin'
                : window.solo?.desktop
                    ? window.solo.desktop.deliverSession(
                        ${JSON.stringify(session.token)}, ${JSON.stringify(session.user ?? null)})
                    : 'no-interop'`, true);
    } catch {
        // Renderer navigating or gone; the next sign-in attempt starts fresh.
        return;
    }

    if (outcome === 'wrong-origin' || win.isDestroyed()) {
        return;
    }

    if (outcome === 'stored' || outcome === 'no-interop') {
        // The session is in storage but no .NET receiver was registered (page
        // mid-boot, interop missing). A reload boots straight into the
        // signed-in state instead of leaving the UI stuck.
        contents.reload();
    }

    win.show();
    win.focus();
    app.focus({ steal: true });
}

// Surfaces a failed browser flow in the sign-in UI — a silent return would
// leave the user staring at "finish signing in in your browser" forever.
async function notifySignInFailed(instance, message) {
    console.warn(`Sign-in to ${instance.url} failed: ${message}`);
    if (settingsSignInId === instance.id) {
        sendToSettings('sign-in:failed', { id: instance.id, message });
    }

    const contents = windowFor(instance)?.webContents;
    if (!contents || contents.isDestroyed()) {
        return;
    }

    try {
        await contents.executeJavaScript(
            `window.solo?.desktop?.signInFailed(${JSON.stringify(message)})`, true);
    } catch {
        // Renderer navigating or gone — nowhere to show it.
    }
}

// ---------------------------------------------------------------------------
// Settings window
// ---------------------------------------------------------------------------

function openSettingsWindow() {
    if (settingsWindow) {
        settingsWindow.focus();
        return;
    }

    const useGlassBackdrop = nativeEffectsEnabled();
    const settingsLiquidGlass = useGlassBackdrop ? glassModule() : null;
    const useLiquidGlass = settingsLiquidGlass !== null;
    const useVibrancyFallback = useGlassBackdrop && !useLiquidGlass;

    settingsWindow = new BrowserWindow({
        width: 520,
        height: 800,
        resizable: false,
        minimizable: false,
        fullscreenable: false,
        show: false,
        backgroundColor: useGlassBackdrop
            ? '#00000000'
            : nativeTheme.shouldUseDarkColors ? '#000000' : '#f7f4ee',
        transparent: useGlassBackdrop,
        ...(useVibrancyFallback
            ? { vibrancy: 'under-window', visualEffectState: 'active' }
            : {}),
        ...(useGlassBackdrop
            ? {
                titleBarStyle: 'hiddenInset',
                trafficLightPosition: { x: 18, y: 20 },
            }
            : {}),
        title: `${app.getName()} — Settings`,
        // No parent window. Settings belongs to the app rather than to any one
        // instance, and a child window closes with its parent — which here
        // would be the very window that removing an instance closes.
        webPreferences: {
            preload: path.join(__dirname, 'preload', 'settings.js'),
            ...(useGlassBackdrop ? { additionalArguments: ['--solo-glass-backdrop'] } : {}),
            ...HARDENED_WEB_PREFERENCES,
        },
    });

    if (useGlassBackdrop) {
        settingsWindow.setWindowButtonVisibility(true);
    }

    if (useLiquidGlass) {
        settingsWindow.webContents.once('did-finish-load', () => {
            try {
                const viewId = settingsLiquidGlass.addView(settingsWindow.getNativeWindowHandle());
                if (viewId < 0) {
                    throw new Error('the native add-on returned no view');
                }
                settingsWindow.webContents.send('window:glass-backdrop', true);
                console.log(`Solo settings Liquid Glass enabled (view ${viewId}).`);
            } catch (err) {
                console.error('Solo settings Liquid Glass could not be applied; using Electron vibrancy.', err);
                try {
                    settingsWindow?.setVibrancy('under-window');
                    settingsWindow?.webContents.send('window:glass-backdrop', true);
                    console.log('Solo settings glass fallback enabled (Electron vibrancy).');
                } catch (fallbackError) {
                    settingsWindow?.webContents.send('window:glass-backdrop', false);
                    console.error('Solo settings could not apply its glass fallback.', fallbackError);
                }
            }
        });
    } else if (useVibrancyFallback) {
        console.log('Solo settings glass fallback enabled (Electron vibrancy).');
    }

    settingsWindow.once('ready-to-show', () => settingsWindow.show());
    settingsWindow.on('closed', () => {
        settingsWindow = null;
        // A sign-in still open in the browser carries on without it, and
        // lands in the instance's own window like any other.
        settingsSignInId = null;
        // Drops a slider preview that was never saved; after a save this is
        // the value the instance windows already have.
        sendBackgroundAlpha(settings.backgroundAlpha);
    });
    // Pasting an instance URL is most of the job of this window.
    attachContextMenu(settingsWindow.webContents);
    settingsWindow.setMenuBarVisibility(false);
    // A local form with no links: it navigates nowhere and opens nothing.
    settingsWindow.webContents.on('will-navigate', event => event.preventDefault());
    settingsWindow.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    settingsWindow.loadFile(path.join(__dirname, 'settings', 'settings.html'));
}

// How much of the theme's page background the instance windows paint over
// their glass — every one of them, since the setting is the app's and not an
// instance's. The renderer only uses it while it has the glass backdrop, so
// there is nothing to guard here when native effects are off.
function sendBackgroundAlpha(alpha) {
    for (const win of windows.keys()) {
        if (!win.webContents.isDestroyed()) {
            win.webContents.send('window:glass-background-alpha', alpha);
        }
    }
}

// Asks an address for its anonymous bootstrap config, which is both the test
// of whether it is a Solo instance and where its display name comes from.
// This runs in the main process: the settings page is a file:// renderer, so a
// cross-origin fetch from there would need the very CORS headers this shell
// exists to avoid.
async function fetchInstanceConfig(url) {
    try {
        const response = await net.fetch(`${url}/api/config/auth`, {
            signal: AbortSignal.timeout(8000),
            cache: 'no-store',
        });
        if (!response.ok) {
            return { ok: false, error: `The server answered ${response.status} — is this a Solo instance?` };
        }

        const config = await response.json();
        if (typeof config?.setupCompleted !== 'boolean') {
            return { ok: false, error: 'The server answered, but not like a Solo instance.' };
        }

        return { ok: true, config };
    } catch (err) {
        return { ok: false, error: connectionError(err, process.platform, app.getName()) };
    }
}

// The browser flow is Google's: the instance's /desktop-auth page runs Google
// Identity Services. Any other provider redirects the window itself, and an
// instance still in setup has nobody to sign in as — both are left to the
// client, in the instance's window.
function signsInInBrowser(config) {
    return config.setupCompleted &&
        typeof config.provider === 'string' && config.provider.toLowerCase() === 'google' &&
        Boolean(config.authority) && Boolean(config.clientId);
}

// Adding an instance, in the order the settings page walks the user through:
// the address is tested, the instance is saved, they sign in to it in the
// browser, and then it opens in a window of its own (see deliverSession).
async function addInstance(rawUrl) {
    const url = settingsStore.normalizeInstanceUrl(rawUrl);
    if (!url) {
        return { ok: false, error: 'Enter a valid http(s) address.' };
    }
    if (settings.instances.some(instance => instance.url === url)) {
        return { ok: false, error: 'That instance is already in the list.' };
    }

    const test = await fetchInstanceConfig(url);
    if (!test.ok) {
        return test;
    }

    const instance = settingsStore.createInstance(url, null, test.config.displayName);
    try {
        await claimPort(instance);
    } catch (err) {
        return { ok: false, error: `Could not start a local server for it: ${err.message}` };
    }
    await clearLocalStorage(instance);

    settings.instances.push(instance);
    settingsStore.save(settings);
    refreshInstanceViews();

    if (signsInInBrowser(test.config)) {
        startSettingsSignIn(instance);
        return { ok: true, id: instance.id, name: instance.name, signIn: true };
    }

    await openAndCloseSettings(instance);
    return { ok: true, id: instance.id, name: instance.name, signIn: false };
}

function startSettingsSignIn(instance) {
    settingsSignInId = instance.id;
    beginBrowserSignIn(instance).catch(err => notifySignInFailed(instance, err.message));
}

// Instance window before closing settings: with quit-on-all-closed, the
// reverse order would briefly leave zero windows and end the app.
async function openAndCloseSettings(instance) {
    settingsSignInId = null;
    if (await openInstanceWindow(instance)) {
        settingsWindow?.close();
    }
}

async function removeInstance(instance) {
    signIns.get(instance.id)?.cancel();
    signIns.delete(instance.id);
    if (settingsSignInId === instance.id) {
        settingsSignInId = null;
    }

    settings.instances = settings.instances.filter(other => other !== instance);
    if (settings.lastInstanceId === instance.id) {
        settings.lastInstanceId = null;
    }
    settingsStore.save(settings);

    // Out of the list before its window goes, so the views refreshed by the
    // window closing already show it gone. destroy rather than close: there
    // is nothing left for the page to save to.
    windowFor(instance)?.destroy();
    stopServer(instance);
    await clearLocalStorage(instance);
    refreshInstanceViews();
}

function registerIpc() {
    registerAboutIpc();

    // The settings page's handlers change what the app is connected to and
    // signed in to, so they answer the settings window and nothing else — an
    // instance window renders other people's markdown.
    const handleSettings = (channel, handler) => ipcMain.handle(channel, (event, ...args) => {
        if (!settingsWindow || event.sender !== settingsWindow.webContents) {
            throw new Error(`${channel} is only available to the settings window.`);
        }
        return handler(...args);
    });

    // The window an instance page's message came from, if it is one.
    const instanceWindowOf = event => {
        const win = BrowserWindow.fromWebContents(event.sender);
        return win && windows.has(win) ? win : null;
    };
    const instanceOf = event => windows.get(instanceWindowOf(event))?.instance ?? null;

    handleSettings('settings:get', () => ({
        appName: app.getName(),
        instances: instanceList(),
        nativeEffects: settings.nativeEffects,
        backgroundAlpha: settings.backgroundAlpha,
        platform: process.platform,
        firstRun: settings.instances.length === 0,
    }));

    handleSettings('settings:native-effects', enabled => {
        settings.nativeEffects = enabled !== false;
        settingsStore.save(settings);
    });

    // The slider shows its effect on the instance windows while it is dragged.
    // Nothing is stored until it is let go (below); closing the window puts
    // the saved value back (see openSettingsWindow).
    ipcMain.on('settings:preview-background-alpha', (event, alpha) => {
        if (settingsWindow && event.sender === settingsWindow.webContents) {
            sendBackgroundAlpha(settingsStore.normalizeBackgroundAlpha(alpha));
        }
    });

    // Applies at once, with no restart: unlike the effect itself, the opacity
    // is only a custom property the preload sets.
    handleSettings('settings:background-alpha', alpha => {
        settings.backgroundAlpha = settingsStore.normalizeBackgroundAlpha(alpha);
        settingsStore.save(settings);
        sendBackgroundAlpha(settings.backgroundAlpha);
    });

    // With no instance window behind it, closing the settings window quits via
    // window-all-closed (the settings page labels the button "Quit").
    handleSettings('settings:close', () => {
        settingsWindow?.close();
    });

    handleSettings('instances:add', async rawUrl => {
        if (addingInstance) {
            return { ok: false, error: 'Still adding the last one.' };
        }

        addingInstance = true;
        try {
            return await addInstance(rawUrl);
        } finally {
            addingInstance = false;
        }
    });

    // The settings page's "Sign in again", after a first sign-in failed.
    handleSettings('instances:sign-in', id => {
        const instance = findInstance(id);
        if (instance) {
            startSettingsSignIn(instance);
        }
    });

    // And its way out of one: open the new instance without a session, and
    // sign in on the client's own sign-in page instead.
    handleSettings('instances:open-new', async id => {
        const instance = findInstance(id);
        if (instance) {
            await openAndCloseSettings(instance);
        }
    });

    handleSettings('instances:open', id => requestOpenInstance(id));

    handleSettings('instances:remove', async id => {
        const instance = findInstance(id);
        if (!instance) {
            return;
        }

        const { response } = await dialog.showMessageBox(settingsWindow, {
            type: 'warning',
            buttons: ['Remove', 'Cancel'],
            defaultId: 1,
            cancelId: 1,
            message: `Remove ${instance.name}?`,
            detail: 'Solo forgets the instance and its sign-in on this computer' +
                (windowFor(instance) ? ', and closes its window' : '') +
                '. Nothing on the instance itself changes.',
        });
        if (response === 0 && settings.instances.includes(instance)) {
            await removeInstance(instance);
        }
    });

    // The client is served from this shell's local origin and reaches the API
    // through the proxy, so it cannot work out the instance's own address —
    // which is what a user has to paste into a tool running outside the app.
    ipcMain.handle('instance:url', event => instanceOf(event)?.url ?? null);

    ipcMain.handle('desktop-auth:begin', event => {
        const instance = instanceOf(event);
        return instance ? beginBrowserSignIn(instance) : undefined;
    });

    // Liquid Glass and vibrancy inherit Electron's native appearance. The
    // Blazor theme picker owns this choice, so keep AppKit on that same source
    // instead of leaving it tied to macOS when Solo is explicitly light/dark.
    // The appearance is app-wide, so a window that isn't the current one only
    // has its choice remembered until it is (see onWindowFocused).
    ipcMain.on('window:theme-source', (event, source) => {
        const win = instanceWindowOf(event);
        if (!win || !['system', 'light', 'dark'].includes(source)) {
            return;
        }

        windows.get(win).themeSource = source;
        if (win === currentWindow()) {
            nativeTheme.themeSource = source;
        }
    });

    // Asked for once per page load: the window may already be fullscreen when
    // the renderer boots, and the change events above only cover transitions.
    ipcMain.handle('window:full-screen', event => instanceWindowOf(event)?.isFullScreen() ?? false);

    // A page's own right-click menu, drawn natively (see popupClientMenu).
    // Only an instance window's: the settings page has no menus of its own.
    ipcMain.handle('context-menu:show', (event, items) =>
        instanceWindowOf(event) ? popupClientMenu(event.sender, items) : null);
}
