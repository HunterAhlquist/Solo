// The local origin the desktop app lives on. Serves the published Blazor
// wwwroot and reverse-proxies API traffic to the configured Solo instance, so
// the client runs exactly as it does in the hosted build: same-origin REST,
// same-origin SignalR, no CORS anywhere. The instance URL can change at
// runtime (settings window); the proxy reads it per-request via getTarget.
const http = require('http');
const fs = require('fs');
const path = require('path');
const httpProxy = require('http-proxy');

// /healthz is included so a curious user can sanity-check connectivity from
// the app's own origin; the client itself never calls it.
const PROXY_PREFIXES = ['/api/', '/hubs/'];
const PROXY_EXACT = ['/healthz'];

// The client shipped inside the app renders the same user-written markdown the
// hosted build does, so the local origin gets the same policy the API sends
// (Solo.Api/SecurityHeaders.cs) — keep the two in step. 'wasm-unsafe-eval' is
// Blazor's WebAssembly runtime; the accounts.google.com entries cover the
// client's sign-in UI, which is loaded even though the shell completes sign-in
// in the system browser.
const CONTENT_SECURITY_POLICY = [
    "default-src 'self'",
    "script-src 'self' 'wasm-unsafe-eval' https://accounts.google.com",
    "style-src 'self' 'unsafe-inline' https://accounts.google.com",
    "img-src 'self' data: https:",
    "font-src 'self' data:",
    // 'self' covers the same-origin SignalR WebSocket (CSP3 resolves it to the
    // page origin's ws/wss); no bare ws:/wss: wildcard to any host.
    "connect-src 'self' https://accounts.google.com",
    "frame-src https://accounts.google.com",
    "worker-src 'self' blob:",
    "frame-ancestors 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    "object-src 'none'",
].join('; ');

const SECURITY_HEADERS = {
    'Content-Security-Policy': CONTENT_SECURITY_POLICY,
    'X-Content-Type-Options': 'nosniff',
    'Referrer-Policy': 'strict-origin-when-cross-origin',
    // Kept in step with the API (Solo.Api/SecurityHeaders.cs). Inert on the
    // localhost origin, but keeps the served shell identical to the hosted one.
    'X-Robots-Tag': 'noindex, nofollow',
    // The client needs none of these powerful features. (No HSTS here on
    // purpose: this origin is plain-HTTP localhost, and an HSTS header for
    // 'localhost' would force every other local dev server on the machine to
    // HTTPS — a footgun with no benefit where there is no TLS to enforce.)
    'Permissions-Policy':
        'accelerometer=(), autoplay=(), camera=(), display-capture=(), ' +
        'encrypted-media=(), geolocation=(), gyroscope=(), magnetometer=(), ' +
        'microphone=(), midi=(), payment=(), usb=()',
};

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

function createSoloServer({ clientDir, getTarget }) {
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
        // Only the app itself may talk to this server. A remote page can
        // reach 127.0.0.1 via DNS rebinding, but then the Host header
        // carries the attacker's domain.
        const host = (req.headers.host ?? '').toLowerCase();
        if (!host.startsWith('localhost:') && !host.startsWith('127.0.0.1:')) {
            res.writeHead(403).end();
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
    res.writeHead(200, {
        'Content-Type': type,
        'Cache-Control': 'no-cache',
        ...SECURITY_HEADERS,
    });
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
