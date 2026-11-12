// The local origin the desktop app lives on. Reverse-proxies every path to the
// configured Solo instance — the client the instance serves as well as its
// REST API and SignalR hub — so the window runs exactly the client the
// instance hosts, same-origin, with no CORS anywhere and no client of its own
// to fall out of step with the server. The instance URL can change at runtime
// (settings window); the proxy reads it per-request via getTarget.
//
// Why a local origin at all, rather than pointing the window at the instance:
// each instance keeps an origin of its own (and so its own storage and
// session), and http://localhost is a secure context even when the instance is
// plain http on the LAN, which crypto.subtle, crypto.randomUUID and service
// workers (the client's offline mode) all require.
//
// The instance's own response headers come through untouched, its Content
// Security Policy among them; that policy already knows the instance's
// identity provider (Solo.Api/SecurityHeaders.cs).
const http = require('http');
const crypto = require('crypto');
const httpProxy = require('http-proxy');

// Only the app itself may talk to this server. A remote page can reach
// 127.0.0.1 via DNS rebinding, but then the Host header carries the
// attacker's domain. Checked on the request path and the upgrade path
// alike: a WebSocket to the proxy is as much a way in as a fetch.
function isLocalHost(headers) {
    const host = (headers.host ?? '').toLowerCase();
    return host.startsWith('localhost:') || host.startsWith('127.0.0.1:');
}

// A page load, as opposed to a fetch the client makes. Only a page load gets
// the unreachable page below; the client handles a failed fetch itself.
function isNavigation(req) {
    return req.method === 'GET' && (req.headers['sec-fetch-mode'] === 'navigate' ||
        (req.headers.accept ?? '').includes('text/html'));
}

function createSoloServer({ getTarget }) {
    const proxy = httpProxy.createProxyServer({
        changeOrigin: true, // host-based routing (reverse proxies) needs the real Host
        ws: true,
        xfwd: false,
    });

    proxy.on('error', (err, req, resOrSocket) => {
        if (!(resOrSocket instanceof http.ServerResponse)) {
            resOrSocket.destroy();
            return;
        }
        if (resOrSocket.headersSent) {
            resOrSocket.destroy();
            return;
        }
        if (isNavigation(req)) {
            unreachablePage(resOrSocket, 502, getTarget(), err.code ?? err.message);
            return;
        }
        resOrSocket.writeHead(502, { 'Content-Type': 'application/json; charset=utf-8' });
        resOrSocket.end(JSON.stringify({ error: `Solo instance unreachable: ${err.message}` }));
    });

    const server = http.createServer((req, res) => {
        if (!isLocalHost(req.headers)) {
            res.writeHead(403).end();
            return;
        }

        const target = getTarget();
        if (!target) {
            if (isNavigation(req)) {
                unreachablePage(res, 503, null, null);
                return;
            }
            res.writeHead(503, { 'Content-Type': 'application/json; charset=utf-8' });
            res.end(JSON.stringify({ error: 'No Solo instance configured.' }));
            return;
        }
        proxy.web(req, res, { target });
    });

    // SignalR WebSocket upgrade for /hubs/board.
    server.on('upgrade', (req, socket, head) => {
        // Node hands ownership of the raw socket to this listener, while
        // http-proxy does not watch its errors until the instance accepts the
        // upgrade. Chromium can reset it in that gap after sleep or a network
        // change, so close it here instead of letting ECONNRESET escape.
        socket.once('error', () => socket.destroy());

        const target = getTarget();
        if (!target || !isLocalHost(req.headers)) {
            socket.destroy();
            return;
        }
        proxy.ws(req, socket, head, { target });
    });

    return server;
}

// A 16×16 pixel face, X-eyed, above the unreachable page's heading: drawn at
// 6× in the text colour, so it reads in light and dark alike, with crispEdges
// keeping its pixels square. The same as the iPhone and iPad app's page.
const DEAD_FACE = '<svg class="face" viewBox="0 0 16 16" width="96" height="96" shape-rendering="crispEdges" fill="currentColor" aria-hidden="true"><path d="M2 2h1v1H2zM6 2h1v1H6zM9 2h1v1H9zM13 2h1v1h-1zM3 3h1v1H3zM5 3h1v1H5zM10 3h1v1h-1zM12 3h1v1h-1zM4 4h1v1H4zM11 4h1v1h-1zM3 5h1v1H3zM5 5h1v1H5zM10 5h1v1h-1zM12 5h1v1h-1zM2 6h1v1H2zM6 6h1v1H6zM9 6h1v1H9zM13 6h1v1h-1zM7 9h2v1H7zM6 10h1v3H6zM9 10h1v3H9zM7 13h2v1H7z"/></svg>';

// What a window shows when the instance can't be reached and the client's
// offline mode hasn't cached it (with the cache, its service worker answers
// the page load before it ever gets here). The bundled client used to render
// its own unreachable screen; with the client coming from the instance, this
// is the only thing that can. A nonce lets its one script run under a policy
// that allows nothing else.
function unreachablePage(res, status, target, detail) {
    const nonce = crypto.randomBytes(16).toString('base64');
    const host = target ? safeHost(target) : null;
    const message = host
        ? `Solo couldn't reach ${escapeHtml(host)}. Check your connection, or that the instance is running, then try again.`
        : 'This window has no Solo instance to show. Choose one in Instances.';
    res.writeHead(status, {
        'Content-Type': 'text/html; charset=utf-8',
        'Cache-Control': 'no-store',
        'Content-Security-Policy':
            `default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}'; ` +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
        'X-Content-Type-Options': 'nosniff',
    });
    res.end(`<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Can't reach the instance</title>
<style nonce="${nonce}">
  :root { color-scheme: light dark; font-family: system-ui, -apple-system, "Segoe UI", sans-serif; }
  body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: Canvas; color: CanvasText; }
  main { max-width: 26rem; padding: 2rem; text-align: center; }
  .face { display: block; margin: 0 auto 1.25rem; }
  h1 { font-size: 1.25rem; margin: 0 0 .75rem; }
  p { margin: 0 0 1.5rem; line-height: 1.5; opacity: .8; }
  small { display: block; margin-top: 1.25rem; opacity: .55; }
  button { font: inherit; padding: .5rem 1rem; margin: 0 .25rem; border-radius: 8px; border: 1px solid; cursor: pointer; }
</style>
</head>
<body>
<main>
  ${host ? DEAD_FACE : ''}
  <h1>Can't reach the instance</h1>
  <p>${message}</p>
  <button id="retry" type="button">Try again</button>
  <button id="instances" type="button" hidden>Instances</button>
  ${detail ? `<small>${escapeHtml(String(detail))}</small>` : ''}
</main>
<script nonce="${nonce}">
  document.getElementById('retry').addEventListener('click', () => location.reload());
  const instances = document.getElementById('instances');
  if (window.soloDesktop && window.soloDesktop.openInstances) {
    instances.hidden = false;
    instances.addEventListener('click', () => window.soloDesktop.openInstances());
  }
</script>
</body>
</html>`);
}

function safeHost(target) {
    try {
        return new URL(target).host;
    } catch {
        return null;
    }
}

function escapeHtml(text) {
    return text.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}

module.exports = { createSoloServer };
