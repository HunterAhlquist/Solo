// Solo desktop shell. The Blazor client is served from a local origin
// (http://localhost:{port}) and all /api + /hubs traffic is proxied to the
// Solo instance chosen in the settings window — the client itself is the
// untouched web build. Sign-in happens in the system browser on the
// instance's own origin (main/desktop-auth.js).
const { app, BrowserWindow, Menu, dialog, ipcMain, net, shell } = require('electron');
const path = require('path');
const settingsStore = require('./main/settings');
const { createSoloServer } = require('./main/server');
const { createMcpHandler } = require('./main/mcp');
const { buildMenu } = require('./main/menu');
const { DesktopAuth } = require('./main/desktop-auth');

const desktopAuth = new DesktopAuth();

let settings = null;
let server = null;
let listeningPort = null;
let mainWindow = null;
let settingsWindow = null;

// Isolated profile for automated runs; normal launches use the default.
if (process.env.SOLO_USER_DATA_DIR) {
    app.setPath('userData', process.env.SOLO_USER_DATA_DIR);
}

// Google's sign-in pages refuse "embedded" browsers by user agent. Dropping
// the Electron and app-name tokens leaves a plain Chrome UA, which Google
// accepts. This must happen before any window is created.
app.userAgentFallback = app.userAgentFallback
    .replace(new RegExp(`\\s${app.getName()}/[\\S]+`, 'i'), '')
    .replace(/\sElectron\/[\S]+/, '');

const gotLock = app.requestSingleInstanceLock();
if (!gotLock) {
    app.quit();
} else {
    app.on('second-instance', () => {
        const win = mainWindow ?? settingsWindow;
        if (win) {
            if (win.isMinimized()) win.restore();
            win.focus();
        }
    });

    app.whenReady().then(onReady);
}

function onReady() {
    settings = settingsStore.load();
    registerIpc();
    Menu.setApplicationMenu(buildMenu({ openSettingsWindow }));

    if (settings.instanceUrl) {
        startShell();
    } else {
        // First run: no instance to talk to yet, so settings is the app.
        openSettingsWindow();
    }
}

// Deliberately no macOS lives-in-the-dock exception: Solo is a single-window
// tool, and a closed window should mean a closed app on every platform.
app.on('window-all-closed', () => app.quit());

// ---------------------------------------------------------------------------
// Local server + main window
// ---------------------------------------------------------------------------

function clientDir() {
    return process.env.SOLO_CLIENT_DIR ?? path.join(__dirname, 'wwwroot');
}

async function startShell() {
    try {
        await ensureServer();
    } catch (err) {
        dialog.showErrorBox(
            'Solo could not start its local server',
            `Port ${settings.port} is unavailable (${err.message}).\n` +
            'Change the port in settings.json and restart.');
        app.quit();
        return;
    }

    createMainWindow();
}

function ensureServer() {
    if (server && listeningPort === settings.port) {
        return Promise.resolve();
    }

    const start = () => new Promise((resolve, reject) => {
        // The proxy target is read per-request, so saving a new instance URL
        // needs no server restart — only a page reload.
        server = createSoloServer({
            clientDir: clientDir(),
            getTarget: () => settings.instanceUrl,
            mcpHandler: createMcpHandler({
                getTarget: () => settings.instanceUrl,
                getSessionToken,
                isEnabled: () => settings.mcpEnabled,
            }),
        });
        server.once('error', reject);
        server.listen(settings.port, '127.0.0.1', () => {
            server.removeListener('error', reject);
            listeningPort = settings.port;
            resolve();
        });
    });

    if (server) {
        return new Promise(resolve => server.close(resolve)).then(start);
    }
    return start();
}

function localUrl() {
    // localhost rather than 127.0.0.1: for non-Google OIDC providers this is
    // still the redirect origin operators register, so it must be stable and
    // predictable. (Google sign-in runs on the instance's own origin.)
    return `http://localhost:${listeningPort}/`;
}

function createMainWindow() {
    if (mainWindow) {
        mainWindow.focus();
        return;
    }

    mainWindow = new BrowserWindow({
        width: 1440,
        height: 920,
        minWidth: 900,
        minHeight: 600,
        show: false,
        backgroundColor: '#000000',
        title: 'Solo',
        webPreferences: {
            preload: path.join(__dirname, 'preload', 'desktop.js'),
        },
    });

    mainWindow.once('ready-to-show', () => mainWindow.show());
    mainWindow.on('closed', () => { mainWindow = null; });

    // The client's "Authorize in browser" button starts sign-in over IPC, but
    // if Google's widget ever runs in-app anyway, its popup can never succeed
    // (Google blocks sign-in inside Electron outright) — reroute it into the
    // same browser flow. Every other link belongs in the system browser too.
    mainWindow.webContents.setWindowOpenHandler(({ url }) => {
        if (new URL(url).hostname === 'accounts.google.com') {
            beginBrowserSignIn();
        } else {
            shell.openExternal(url);
        }
        return { action: 'deny' };
    });

    mainWindow.loadURL(localUrl());
}

function beginBrowserSignIn() {
    return desktopAuth.begin({
        instanceUrl: settings.instanceUrl,
        onSession: deliverSession,
        onFailure: notifySignInFailed,
    });
}

// Hands a browser-won Solo session to the running client via
// `solo.desktop.deliverSession` (wwwroot/js/solo-interop.js): the instance
// verified the sign-in and minted the session server-side, so the client
// only stores it and refreshes — the desktop app then stays signed in on
// the session's sliding expiry, no hourly Google credential involved.
async function deliverSession(session) {
    if (!mainWindow) {
        return;
    }

    const contents = mainWindow.webContents;
    if (contents.isLoading()) {
        await new Promise(resolve => contents.once('did-finish-load', resolve));
    }

    let outcome;
    try {
        outcome = await contents.executeJavaScript(
            `window.solo?.desktop
                ? window.solo.desktop.deliverSession(
                    ${JSON.stringify(session.token)}, ${JSON.stringify(session.user ?? null)})
                : 'no-interop'`, true);
    } catch {
        // Renderer navigating or gone; the next sign-in attempt starts fresh.
        return;
    }

    if (outcome === 'stored' || outcome === 'no-interop') {
        // The session is in storage but no .NET receiver was registered (page
        // mid-boot, interop missing). A reload boots straight into the
        // signed-in state instead of leaving the UI stuck.
        contents.reload();
    }

    mainWindow.show();
    mainWindow.focus();
    app.focus({ steal: true });
}

// Surfaces a failed browser flow in the sign-in UI — a silent return would
// leave the user staring at "finish signing in in your browser" forever.
async function notifySignInFailed(message) {
    const contents = mainWindow?.webContents;
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

// The MCP endpoint (main/mcp.js) authenticates its Solo API calls with the
// session token the signed-in client keeps in web storage — read from the
// renderer on demand rather than copied anywhere, so MCP access tracks the
// app's sign-in state exactly: sign out (or close the window) and it's gone.
async function getSessionToken() {
    const contents = mainWindow?.webContents;
    if (!contents || contents.isDestroyed() || contents.isLoading()) {
        return null;
    }

    try {
        const token = await contents.executeJavaScript(
            'window.solo?.session?.token() ?? null', true);
        return typeof token === 'string' && token.startsWith('solo_') ? token : null;
    } catch {
        // Renderer navigating or gone — same as not signed in.
        return null;
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

    settingsWindow = new BrowserWindow({
        width: 520,
        height: 580,
        resizable: false,
        minimizable: false,
        fullscreenable: false,
        show: false,
        backgroundColor: '#000000',
        title: 'Solo — Server Settings',
        parent: mainWindow ?? undefined,
        webPreferences: {
            preload: path.join(__dirname, 'preload', 'settings.js'),
        },
    });

    settingsWindow.once('ready-to-show', () => settingsWindow.show());
    settingsWindow.on('closed', () => { settingsWindow = null; });
    settingsWindow.setMenuBarVisibility(false);
    settingsWindow.loadFile(path.join(__dirname, 'settings', 'settings.html'));
}

function registerIpc() {
    ipcMain.handle('settings:get', () => ({
        instanceUrl: settings.instanceUrl,
        port: settings.port,
        mcpEnabled: settings.mcpEnabled,
        firstRun: !settings.instanceUrl,
    }));

    // The connection test runs in the main process: the settings page is a
    // file:// renderer, so a cross-origin fetch from there would need the
    // very CORS headers this shell exists to avoid.
    ipcMain.handle('settings:test', async (_event, rawUrl) => {
        const url = settingsStore.normalizeInstanceUrl(rawUrl);
        if (!url) {
            return { ok: false, error: 'Enter a valid http(s) address.' };
        }

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

            return {
                ok: true,
                url,
                provider: config.provider || null,
                setupCompleted: config.setupCompleted,
            };
        } catch (err) {
            return { ok: false, error: `Could not reach the server: ${err.message}` };
        }
    });

    ipcMain.handle('settings:save', async (_event, rawUrl, mcpEnabled) => {
        const url = settingsStore.normalizeInstanceUrl(rawUrl);
        if (!url) {
            return { ok: false, error: 'Enter a valid http(s) address.' };
        }

        const urlChanged = url !== settings.instanceUrl;
        settings.instanceUrl = url;
        settings.mcpEnabled = mcpEnabled === true;
        settingsStore.save(settings);

        // Main window before closing settings: with quit-on-all-closed, the
        // reverse order would briefly leave zero windows and end the app.
        if (mainWindow) {
            if (urlChanged) {
                // New instance, fresh boot: the client fetches auth config on
                // load. An MCP-only change needs nothing — the handler reads
                // the flag per request.
                mainWindow.webContents.loadURL(localUrl());
            }
            mainWindow.focus();
        } else {
            await startShell();
        }
        settingsWindow?.close();
        return { ok: true };
    });

    // On first run there is nothing behind the settings window, so closing it
    // quits via window-all-closed (the settings page labels the button "Quit").
    ipcMain.handle('settings:close', () => {
        settingsWindow?.close();
    });

    ipcMain.handle('desktop-auth:begin', () => beginBrowserSignIn());
}
