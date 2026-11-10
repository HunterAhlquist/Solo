import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { createSoloServer } = require('../main/server');

// The local server answers only the app's own origin. A page on another site
// that has rebound its DNS name to 127.0.0.1 reaches the port, but its
// requests carry that name in Host — and must be refused on both ways in:
// the request handler, and the WebSocket upgrade the hub uses, which Node
// routes past the request handler altogether.
// Stands in for the instance: records which requests and upgrades reach it.
const reached = [];
const upstream = http.createServer((req, res) => {
    reached.push(`http ${req.url}`);
    res.writeHead(200, { 'Content-Type': 'application/json' }).end('{}');
});
upstream.on('upgrade', (req, socket) => {
    reached.push(`ws ${req.url}`);
    socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n');
});
await listen(upstream);

const desktop = createSoloServer({
    getTarget: () => `http://127.0.0.1:${upstream.address().port}`,
});
await listen(desktop);
const { port } = desktop.address();
const ownHost = `localhost:${port}`;
const foreignHost = `solo-desktop.attacker.example:${port}`;

try {
    // The app's own origin is proxied, the client's files as well as the API.
    assert.equal((await get('/', ownHost)).status, 200);
    assert.equal((await get('/api/users/me', ownHost)).status, 200);
    assert.deepEqual(reached.splice(0), ['http /', 'http /api/users/me']);

    // Another name for the same port is refused before the proxy.
    assert.equal((await get('/', foreignHost)).status, 403);
    assert.equal((await get('/api/users/me', foreignHost)).status, 403);
    assert.deepEqual(reached.splice(0), []);

    // An upgrade from the app's origin reaches the instance...
    assert.equal(await upgrade('/hubs/board', ownHost), 'answered');
    assert.deepEqual(reached.splice(0), ['ws /hubs/board']);

    // ...and one carrying a foreign name is dropped without being proxied.
    assert.equal(await upgrade('/hubs/board', foreignHost), 'closed');
    assert.equal(await upgrade('/api/ext/anything', foreignHost), 'closed');
    assert.deepEqual(reached.splice(0), []);

    console.log('Desktop server refused the foreign Host on the request path and the upgrade path.');
} finally {
    desktop.closeAllConnections();
    await close(desktop);
    upstream.closeAllConnections();
    await close(upstream);
}

function get(target, host) {
    return new Promise((resolve, reject) => {
        http.get({ host: '127.0.0.1', port, path: target, headers: { Host: host } }, res => {
            res.resume();
            res.on('end', () => resolve({ status: res.statusCode }));
        }).on('error', reject);
    });
}

// A raw upgrade request: 'answered' when a response came back (the upstream's
// refusal, proxied through), 'closed' when the socket ended with nothing.
function upgrade(target, host) {
    return new Promise((resolve, reject) => {
        const socket = net.connect(port, '127.0.0.1');
        let received = '';
        socket.on('connect', () => socket.write(
            `GET ${target} HTTP/1.1\r\nHost: ${host}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n` +
            'Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n'));
        socket.on('data', chunk => received += chunk);
        socket.on('close', () => resolve(received.length > 0 ? 'answered' : 'closed'));
        socket.on('error', reject);
        socket.setTimeout(5000, () => { socket.destroy(); reject(new Error(`no answer for ${target} from ${host}`)); });
    });
}

function listen(server) {
    return new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => {
            server.removeListener('error', reject);
            resolve();
        });
    });
}

function close(server) {
    return new Promise((resolve, reject) => {
        server.close(error => error ? reject(error) : resolve());
    });
}
