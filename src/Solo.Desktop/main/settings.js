// Persistent shell settings: a single JSON file in Electron's userData
// directory. Native effects are opt-out so older settings files retain the
// desktop treatment introduced after they were created. The background alpha
// is how much of the theme's page background is painted over that treatment:
// 0 is bare glass, which is also what a settings file from before it reads as.
//
// Each instance carries a local port of its own, because the port is what
// keeps instances apart: the client stores its session in localStorage, which
// Chromium scopes to the origin, so one origin per instance is what stops a
// session minted by one server from ever being sent to another. The port is
// persisted rather than picked at launch for the same reason — a different
// port is a different origin, and the sign-in stored under the old one is
// gone. It is also the redirect origin a non-Google OIDC provider requires to
// be registered. `port` at the top level is where the numbering starts.
const { app } = require('electron');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

// An unpackaged run (`npm start`, the check scripts) defaults to the next port
// up, so it can start while the installed app holds the usual one. Only the
// default moves: a port already written to settings.json is used as it is.
const DEFAULT_PORT = app.isPackaged ? 41321 : 41322;
const DEFAULT_BACKGROUND_ALPHA = 0;
// InstanceBranding.MaxDisplayNameLength in Solo.Shared.
const MAX_DISPLAY_NAME_LENGTH = 40;

function file() {
    return path.join(app.getPath('userData'), 'settings.json');
}

function load() {
    let raw;
    try {
        raw = JSON.parse(fs.readFileSync(file(), 'utf8'));
    } catch {
        raw = {};
    }

    const port = validPort(raw.port) ? raw.port : DEFAULT_PORT;
    const instances = Array.isArray(raw.instances)
        ? readInstances(raw.instances)
        : migrateSingleInstance(raw.instanceUrl, port);

    return {
        port,
        nativeEffects: raw.nativeEffects !== false,
        backgroundAlpha: normalizeBackgroundAlpha(raw.backgroundAlpha),
        instances,
        lastInstanceId: instances.some(i => i.id === raw.lastInstanceId)
            ? raw.lastInstanceId
            : null,
    };
}

// Settings written before the shell knew more than one instance held a lone
// `instanceUrl`, served on the top-level port. Keeping that port keeps the
// origin, and with it the sign-in already stored there.
function migrateSingleInstance(instanceUrl, port) {
    const url = normalizeInstanceUrl(instanceUrl);
    return url ? [createInstance(url, port)] : [];
}

// The file is hand-editable, so entries are checked rather than trusted. A
// second entry claiming an id or port already taken is dropped: two instances
// on one port would share an origin, which is the one thing this file exists
// to prevent.
function readInstances(entries) {
    const instances = [];
    for (const entry of entries) {
        const url = normalizeInstanceUrl(entry?.url);
        if (!url || !validPort(entry.port) ||
            typeof entry.id !== 'string' || !entry.id ||
            instances.some(i => i.id === entry.id || i.port === entry.port)) {
            continue;
        }

        instances.push({
            id: entry.id,
            // The name the instance last gave, kept so the menu is right
            // before — and without — the instance being reachable.
            name: instanceName(url, entry.name),
            url,
            port: entry.port,
        });
    }
    return instances;
}

function createInstance(url, port, displayName) {
    return { id: crypto.randomUUID(), name: instanceName(url, displayName), url, port };
}

// What the instance is called in menus and window titles: the display name
// its own server gives it (Admin settings, served with /api/config/auth), and
// for an instance without one, its address without the scheme.
//
// The display name is another machine's input headed for native menus and
// dialogs, so it is cut down to what the API itself would have accepted —
// no control characters, 40 at most — rather than taken on trust.
function instanceName(url, displayName) {
    const own = typeof displayName === 'string'
        ? displayName.replace(/[\u0000-\u001f\u007f]/g, '').trim().slice(0, MAX_DISPLAY_NAME_LENGTH).trim()
        : '';
    return own || instanceAddress(url);
}

function instanceAddress(url) {
    const { host, pathname } = new URL(url);
    return host + pathname.replace(/\/+$/, '');
}

function validPort(port) {
    return Number.isInteger(port) && port > 0 && port < 65536;
}

// The value ends up in a CSS declaration in the renderer, and it arrives from
// a hand-editable file and from the settings page — so it is only ever a
// finite number in [0, 1], whatever was supplied.
function normalizeBackgroundAlpha(input) {
    return typeof input === 'number' && Number.isFinite(input)
        ? Math.min(1, Math.max(0, input))
        : DEFAULT_BACKGROUND_ALPHA;
}

function save(settings) {
    fs.mkdirSync(path.dirname(file()), { recursive: true });
    fs.writeFileSync(file(), JSON.stringify(settings, null, 2) + '\n');
}

// Accepts what a person pastes — with or without scheme, trailing slash, or a
// stray path — and returns a clean origin(+path) URL, or null when it cannot
// be a Solo instance address at all.
function normalizeInstanceUrl(input) {
    if (typeof input !== 'string' || !input.trim()) {
        return null;
    }

    let candidate = input.trim();
    if (!/^https?:\/\//i.test(candidate)) {
        candidate = 'https://' + candidate;
    }

    let url;
    try {
        url = new URL(candidate);
    } catch {
        return null;
    }

    if (url.protocol !== 'http:' && url.protocol !== 'https:') {
        return null;
    }

    // Keep a subdirectory if the instance is hosted under one, but drop
    // query/hash and the trailing slash so proxy path joins stay predictable.
    const pathname = url.pathname.replace(/\/+$/, '');
    return url.origin + pathname;
}

module.exports = {
    load, save, normalizeInstanceUrl, normalizeBackgroundAlpha,
    createInstance, instanceName, instanceAddress, DEFAULT_PORT,
};
