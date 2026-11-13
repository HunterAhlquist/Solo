import assert from 'node:assert/strict';
import http from 'node:http';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { createSoloServer } = require('../main/server');

// The shell bundles no client: every path on the local origin, the client's
// own files included, comes from the instance, with the instance's headers.
// When the instance can't be reached, a page load gets the shell's own
// unreachable page and a fetch gets JSON the client already understands.
const upstream = http.createServer((req, res) => {
    res.writeHead(200, {
        'Content-Type': 'text/plain; charset=utf-8',
        'Content-Security-Policy': "default-src 'self'; connect-src 'self' https://id.example.com",
        'X-Upstream-Host': req.headers.host,
    });
    res.end(`UPSTREAM ${req.method} ${req.url}`);
});
const upstreamPort = await listen(upstream);
const target = `http://127.0.0.1:${upstreamPort}`;

let current = target;
const desktop = createSoloServer({ getTarget: () => current });
const port = await listen(desktop);

try {
    const paths = [
        '/',
        '/index.html',
        '/_framework/blazor.webassembly.js',
        '/css/app.css?v=1',
        '/service-worker.js',
        '/projects/DMD/boards/MAIN?task=3',
        '/api/config/auth',
        '/api/extensions/design-docs/content/design-docs.js',
        '/hubs/board/negotiate?negotiateVersion=1',
        '/healthz',
        '/desktop-auth?state=abc',
    ];
    for (const path of paths) {
        const result = await request(path);
        assert.equal(result.status, 200, path);
        assert.equal(result.body, `UPSTREAM GET ${path}`, path);
        // The instance's policy, not one of the shell's own.
        assert.equal(result.headers['content-security-policy'],
            "default-src 'self'; connect-src 'self' https://id.example.com", path);
        // changeOrigin: the instance sees its own Host, which a reverse proxy in front of it needs.
        assert.equal(result.headers['x-upstream-host'], `127.0.0.1:${upstreamPort}`, path);
    }

    const posted = await request('/api/tasks', { method: 'POST', body: '{"title":"x"}' });
    assert.equal(posted.body, 'UPSTREAM POST /api/tasks');

    // The instance goes away.
    upstream.closeAllConnections();
    await close(upstream);

    const page = await request('/projects/DMD', { headers: { Accept: 'text/html', 'Sec-Fetch-Mode': 'navigate' } });
    assert.equal(page.status, 502);
    assert.match(page.headers['content-type'], /^text\/html/);
    assert.equal(page.headers['cache-control'], 'no-store');
    assert.match(page.body, /Can't reach the instance/);
    assert.match(page.body, new RegExp(`127\\.0\\.0\\.1:${upstreamPort}`));
    assert.match(page.body, /id="retry"/);
    // Its one script runs by nonce; nothing else may.
    const csp = page.headers['content-security-policy'];
    const nonce = /script-src 'nonce-([^']+)'/.exec(csp)?.[1];
    assert.ok(nonce, csp);
    assert.match(csp, /default-src 'none'/);
    assert.ok(page.body.includes(`<script nonce="${nonce}">`));

    const fetched = await request('/api/projects', { headers: { Accept: 'application/json' } });
    assert.equal(fetched.status, 502);
    assert.match(fetched.headers['content-type'], /^application\/json/);
    assert.match(JSON.parse(fetched.body).error, /Solo instance unreachable/);

    // No instance configured at all.
    current = null;
    const none = await request('/', { headers: { Accept: 'text/html' } });
    assert.equal(none.status, 503);
    assert.match(none.body, /no Solo instance to show/);
    const noneFetch = await request('/api/config/auth', { headers: { Accept: 'application/json' } });
    assert.equal(noneFetch.status, 503);
    assert.match(noneFetch.body, /No Solo instance configured/);

    // An instance address can't write into the page.
    current = 'http://"><script>alert(1)</script>.invalid';
    const hostile = await request('/', { headers: { Accept: 'text/html' } });
    assert.ok(!hostile.body.includes('<script>alert(1)</script>'), hostile.body);

    console.log(`PASS: ${paths.length} client, API, hub and page paths proxied with the instance's headers; ` +
        'an unreachable or missing instance shows the unreachable page to a page load and JSON to a fetch.');
} finally {
    desktop.closeAllConnections();
    await close(desktop);
}

function request(path, { method = 'GET', headers = {}, body } = {}) {
    return new Promise((resolve, reject) => {
        const req = http.request({ host: '127.0.0.1', port, path, method, headers: { Host: `localhost:${port}`, ...headers } }, res => {
            let text = '';
            res.setEncoding('utf8');
            res.on('data', chunk => text += chunk);
            res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: text }));
        });
        req.on('error', reject);
        req.end(body);
    });
}

function listen(server) {
    return new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => {
            server.removeListener('error', reject);
            resolve(server.address().port);
        });
    });
}

function close(server) {
    return new Promise(resolve => server.close(resolve));
}
