// Persistent shell settings: a single JSON file in Electron's userData
// directory. The local port exists for the rare clash and because non-Google
// OIDC providers redirect back to this origin, which they require to be
// registered. Native effects are opt-out so older settings files retain the
// desktop treatment introduced after they were created. The background alpha
// is how much of the theme's page background is painted over that treatment:
// 0 is bare glass, which is also what a settings file from before it reads as.
const { app } = require('electron');
const fs = require('fs');
const path = require('path');

// An unpackaged run (`npm start`, the check scripts) defaults to the next port
// up, so it can start while the installed app holds the usual one. Only the
// default moves: a port already written to settings.json is used as it is.
const DEFAULT_PORT = app.isPackaged ? 41321 : 41322;
const DEFAULT_BACKGROUND_ALPHA = 0;

function file() {
    return path.join(app.getPath('userData'), 'settings.json');
}

function load() {
    try {
        const raw = JSON.parse(fs.readFileSync(file(), 'utf8'));
        return {
            instanceUrl: normalizeInstanceUrl(raw.instanceUrl) ?? null,
            port: Number.isInteger(raw.port) && raw.port > 0 && raw.port < 65536
                ? raw.port
                : DEFAULT_PORT,
            nativeEffects: raw.nativeEffects !== false,
            backgroundAlpha: normalizeBackgroundAlpha(raw.backgroundAlpha),
        };
    } catch {
        return {
            instanceUrl: null,
            port: DEFAULT_PORT,
            nativeEffects: true,
            backgroundAlpha: DEFAULT_BACKGROUND_ALPHA,
        };
    }
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

module.exports = { load, save, normalizeInstanceUrl, normalizeBackgroundAlpha, DEFAULT_PORT };
