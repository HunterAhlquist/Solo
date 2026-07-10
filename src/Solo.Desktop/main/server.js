// The local origin the desktop app lives on. Serves the published Blazor
// wwwroot and reverse-proxies API traffic to the configured Solo instance, so
// the client runs exactly as it does in the hosted build: same-origin REST,
// same-origin SignalR, no CORS anywhere. The instance URL can change at
// runtime (settings window); the proxy reads it per-request via getTarget.
const http = require('http');
const fs = require('fs');
const path = require('path');
const httpProxy = require('http-proxy');
const { looksLikeJwt } = require('./desktop-auth');

// /healthz is included so a curious user can sanity-check connectivity from
// the app's own origin; the client itself never calls it.
const PROXY_PREFIXES = ['/api/', '/hubs/'];
const PROXY_EXACT = ['/healthz'];

const MIME = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'text/javascript; charset=utf-8',
    '.mjs': 'text/javascript; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.wasm': 'application/wasm',
    '.svg': 'image/svg+xml',
    '.png': 'image/png',
    '.jpg': 'image/jpeg',
    '.ico': 'image/x-icon',
    '.woff': 'font/woff',
    '.woff2': 'font/woff2',
    '.dat': 'application/octet-stream',
    '.pdb': 'application/octet-stream',
    '.txt': 'text/plain; charset=utf-8',
    '.map': 'application/json; charset=utf-8',
    '.webmanifest': 'application/manifest+json; charset=utf-8',
};

function shouldProxy(url) {
    const pathname = url.split('?')[0];
    return PROXY_PREFIXES.some(p => pathname.startsWith(p)) || PROXY_EXACT.includes(pathname);
}

function createSoloServer({ clientDir, getTarget, desktopAuth, onSignInCredential }) {
    const proxy = httpProxy.createProxyServer({
        changeOrigin: true, // host-based routing (reverse proxies) needs the real Host
        ws: true,
        xfwd: false,
    });

    proxy.on('error', (err, req, resOrSocket) => {
        if (resOrSocket instanceof http.ServerResponse) {
            if (!resOrSocket.headersSent) {
                resOrSocket.writeHead(502, { 'Content-Type': 'application/json; charset=utf-8' });
            }
            resOrSocket.end(JSON.stringify({ error: `Solo instance unreachable: ${err.message}` }));
        } else {
            resOrSocket.destroy();
        }
    });

    const server = http.createServer((req, res) => {
        // Only the app itself and the local browser sign-in page may talk to
        // this server. A remote page can reach 127.0.0.1 via DNS rebinding,
        // but then the Host header carries the attacker's domain.
        const host = (req.headers.host ?? '').toLowerCase();
        if (!host.startsWith('localhost:') && !host.startsWith('127.0.0.1:')) {
            res.writeHead(403).end();
            return;
        }

        if (desktopAuth && req.url.split('?')[0].startsWith('/desktop-auth/')) {
            serveDesktopAuth(req, res, { desktopAuth, onSignInCredential });
            return;
        }

        if (shouldProxy(req.url)) {
            const target = getTarget();
            if (!target) {
                res.writeHead(503, { 'Content-Type': 'application/json; charset=utf-8' });
                res.end(JSON.stringify({ error: 'No Solo instance configured.' }));
                return;
            }
            proxy.web(req, res, { target });
            return;
        }

        serveStatic(req, res, clientDir);
    });

    // SignalR WebSocket upgrade for /hubs/board.
    server.on('upgrade', (req, socket, head) => {
        const target = getTarget();
        if (!target || !shouldProxy(req.url)) {
            socket.destroy();
            return;
        }
        proxy.ws(req, socket, head, { target });
    });

    return server;
}

// ---------------------------------------------------------------------------
// Browser sign-in flow (see main/desktop-auth.js for why it exists).
//   GET  /desktop-auth/signin?state={nonce} — the GIS page, opened in the
//        user's default browser; refuses states the app never minted.
//   POST /desktop-auth/complete {state, credential} — called by that page;
//        consumes the nonce and hands the ID token to the main process.
// ---------------------------------------------------------------------------
function serveDesktopAuth(req, res, { desktopAuth, onSignInCredential }) {
    const [pathname, query] = req.url.split('?');

    if (req.method === 'GET' && pathname === '/desktop-auth/signin') {
        const state = new URLSearchParams(query).get('state');
        if (!desktopAuth.isPending(state)) {
            res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
            res.end('This sign-in link has expired. Start again from the Solo app.');
            return;
        }

        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
        fs.createReadStream(path.join(__dirname, '..', 'auth', 'signin.html'))
            .on('error', () => res.destroy())
            .pipe(res);
        return;
    }

    if (req.method === 'POST' && pathname === '/desktop-auth/complete') {
        readJsonBody(req, 16 * 1024)
            .then(body => {
                if (!desktopAuth.consume(body?.state)) {
                    res.writeHead(403).end();
                    return;
                }
                if (!looksLikeJwt(body?.credential)) {
                    res.writeHead(400).end();
                    return;
                }
                onSignInCredential(body.credential);
                res.writeHead(204).end();
            })
            .catch(() => res.writeHead(400).end());
        return;
    }

    res.writeHead(404).end();
}

function readJsonBody(req, limit) {
    return new Promise((resolve, reject) => {
        const chunks = [];
        let size = 0;
        req.on('data', chunk => {
            size += chunk.length;
            if (size > limit) {
                reject(new Error('body too large'));
                req.destroy();
                return;
            }
            chunks.push(chunk);
        });
        req.on('end', () => {
            try {
                resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')));
            } catch (err) {
                reject(err);
            }
        });
        req.on('error', reject);
    });
}

function serveStatic(req, res, clientDir) {
    if (req.method !== 'GET' && req.method !== 'HEAD') {
        res.writeHead(405).end();
        return;
    }

    let pathname;
    try {
        pathname = decodeURIComponent(req.url.split('?')[0]);
    } catch {
        res.writeHead(400).end();
        return;
    }

    let filePath = path.normalize(path.join(clientDir, pathname));
    if (!filePath.startsWith(clientDir + path.sep) && filePath !== clientDir) {
        res.writeHead(403).end();
        return;
    }

    if (!isFile(filePath)) {
        // SPA fallback: any client route (/projects, /board/…) boots index.html.
        filePath = path.join(clientDir, 'index.html');
        if (!isFile(filePath)) {
            res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
            res.end('Client assets not found. Run `npm run build:client` first.');
            return;
        }
    }

    const type = MIME[path.extname(filePath).toLowerCase()] ?? 'application/octet-stream';
    // Local origin, near-zero latency: skip caching entirely so a rebuilt
    // client is always picked up on reload.
    res.writeHead(200, { 'Content-Type': type, 'Cache-Control': 'no-cache' });
    if (req.method === 'HEAD') {
        res.end();
        return;
    }
    fs.createReadStream(filePath)
        .on('error', () => res.destroy())
        .pipe(res);
}

function isFile(p) {
    try {
        return fs.statSync(p).isFile();
    } catch {
        return false;
    }
}

module.exports = { createSoloServer };
