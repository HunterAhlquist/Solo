// Persistent shell settings: a single JSON file in Electron's userData
// directory. The only setting a user normally touches is the instance URL;
// the local port exists for the rare clash and because non-Google OIDC
// providers redirect back to this origin, which they require to be
// registered — so it must stay stable.
const { app } = require('electron');
const fs = require('fs');
const path = require('path');

const DEFAULT_PORT = 41321;

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
        };
    } catch {
        return { instanceUrl: null, port: DEFAULT_PORT };
    }
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

module.exports = { load, save, normalizeInstanceUrl, DEFAULT_PORT };
