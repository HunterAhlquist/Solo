import assert from 'node:assert/strict';
import net from 'node:net';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { createSoloServer } = require('../main/server');

const uncaught = [];
const upstreamSockets = new Set();
const onUncaughtException = error => uncaught.push(error);
process.on('uncaughtException', onUncaughtException);

const upstream = net.createServer(socket => {
    upstreamSockets.add(socket);
    socket.on('error', () => {});
    socket.on('close', () => upstreamSockets.delete(socket));
    // Keep the upstream side of the WebSocket handshake pending. A network
    // transition can leave the real instance in this state while Chromium
    // abandons its localhost connection and starts a fresh SignalR attempt.
    socket.once('data', () => upstream.emit('pending-upgrade'));
});

await listen(upstream);

const desktop = createSoloServer({
    clientDir: process.cwd(),
    getTarget: () => `http://127.0.0.1:${upstream.address().port}`,
});
await listen(desktop);

try {
    for (let attempt = 0; attempt < 3; attempt++) {
        await resetPendingUpgrade(desktop.address().port);
        await delay(50);
    }

    assert.equal(
        uncaught.length,
        0,
        `WebSocket client resets escaped the desktop server:\n${uncaught.map(error => error.stack).join('\n')}`,
    );
    console.log('Desktop server handled 3/3 pending WebSocket upgrade resets.');
} finally {
    process.removeListener('uncaughtException', onUncaughtException);
    desktop.closeAllConnections();
    for (const socket of upstreamSockets) {
        socket.destroy();
    }
    await Promise.all([close(desktop), close(upstream)]);
}

function resetPendingUpgrade(port) {
    return new Promise((resolve, reject) => {
        const onPendingUpgrade = () => {
            client.removeListener('error', onError);
            client.on('error', () => {});
            client.resetAndDestroy();
            resolve();
        };
        const onError = error => {
            upstream.removeListener('pending-upgrade', onPendingUpgrade);
            reject(error);
        };

        const client = net.connect(port, '127.0.0.1');
        upstream.once('pending-upgrade', onPendingUpgrade);
        client.once('error', onError);
        client.once('connect', () => {
            client.write([
                'GET /hubs/board HTTP/1.1',
                `Host: localhost:${port}`,
                'Connection: Upgrade',
                'Upgrade: websocket',
                '',
                '',
            ].join('\r\n'));
        });
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

function delay(milliseconds) {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}
