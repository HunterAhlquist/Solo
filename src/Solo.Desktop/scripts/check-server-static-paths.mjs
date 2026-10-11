import assert from 'node:assert/strict';
import http from 'node:http';
import path from 'node:path';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { createSoloServer } = require('../main/server');

// The served root sits beside a file it must never hand out, and beside a
// sibling whose name merely starts with the root's — the case a prefix test
// without the trailing separator lets through.
const sandbox = mkdtempSync(path.join(tmpdir(), 'solo-static-paths-'));
const clientDir = path.join(sandbox, 'wwwroot');
mkdirSync(path.join(clientDir, 'css'), { recursive: true });
mkdirSync(path.join(sandbox, 'wwwroot-old'));
writeFileSync(path.join(clientDir, 'index.html'), 'INDEX');
writeFileSync(path.join(clientDir, 'css', 'app.css'), 'CSS');
writeFileSync(path.join(sandbox, 'secret.txt'), 'SECRET');
writeFileSync(path.join(sandbox, 'wwwroot-old', 'secret.txt'), 'SECRET');

const desktop = createSoloServer({ clientDir, getTarget: () => null });
await listen(desktop);

try {
    const served = [
        ['/', 'INDEX'],
        ['/index.html', 'INDEX'],
        ['/css/app.css', 'CSS'],
        ['/css/app.css?v=1', 'CSS'],
        // Dot segments that stay inside the root are just a longer spelling.
        ['/css/../css/app.css', 'CSS'],
        // A client route, and a name the filesystem itself rejects, both boot the client.
        ['/projects/DMD/board', 'INDEX'],
        ['/css/app.css%00', 'INDEX'],
    ];
    for (const [target, body] of served) {
        assert.deepEqual(await get(target), { status: 200, body }, target);
    }

    // http.request sends the path as written, so these reach the server
    // un-normalized, the way no browser would send them.
    const refused = [
        '/../secret.txt',
        '/%2e%2e/secret.txt',
        '/css/../../secret.txt',
        '/..%2fsecret.txt',
        '/../wwwroot-old/secret.txt',
        '/..',
        '/css/..',
    ];
    for (const target of refused) {
        assert.deepEqual(await get(target), { status: 403, body: '' }, target);
    }

    console.log(`Desktop server served ${served.length}/${served.length} in-root paths and refused ${refused.length}/${refused.length} escaping ones.`);
} finally {
    desktop.closeAllConnections();
    await close(desktop);
    rmSync(sandbox, { recursive: true, force: true });
}

function get(target) {
    return new Promise((resolve, reject) => {
        const { port } = desktop.address();
        http.get({ host: '127.0.0.1', port, path: target, headers: { Host: `localhost:${port}` } }, res => {
            let body = '';
            res.setEncoding('utf8');
            res.on('data', chunk => body += chunk);
            res.on('end', () => resolve({ status: res.statusCode, body }));
        }).on('error', reject);
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
