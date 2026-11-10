import assert from 'node:assert/strict';
import http from 'node:http';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { createSoloServer } = require('../main/server');
const servers = [];
try {
    const instances = [];
    for (const [id, extensions] of [['first', ['roadmap']], ['second', ['design-docs', 'mcp']], ['third', []]]) {
        const upstream = http.createServer((request, response) => {
            assert.equal(request.headers.authorization, `Bearer fixture-${id}`, 'Credential crossed instances');
            const parts = request.url.split('/');
            if (request.url === '/') {
                response.end(`CLIENT ${id}`);
            } else if (request.url === '/api/extensions') {
                response.setHeader('Content-Type', 'application/json');
                response.end(JSON.stringify(extensions.map(extension => ({ id: extension }))));
            } else if (extensions.includes(parts[3])) {
                response.end(`${id}:${parts[3]}:${parts.slice(4).join('/')}`);
            } else {
                response.writeHead(404).end();
            }
        });
        servers.push(upstream);
        const target = `http://127.0.0.1:${await listen(upstream)}`;
        const desktop = createSoloServer({ getTarget: () => target });
        servers.push(desktop);
        instances.push({ id, extensions, origin: `http://localhost:${await listen(desktop)}` });
    }
    for (let repeat = 0; repeat < 3; repeat++) {
        await Promise.all(instances.map(async ({ id, extensions, origin }) => {
            const headers = { Authorization: `Bearer fixture-${id}` };
            // Each origin runs the client its own instance serves.
            assert.equal(await (await fetch(`${origin}/`, { headers })).text(), `CLIENT ${id}`);
            const inventory = await fetch(`${origin}/api/extensions`, { headers });
            assert.deepEqual((await inventory.json()).map(item => item.id), extensions);
            for (const extension of extensions) {
                for (const route of ['client/Extension.dll', 'content/editor.js?v=1']) {
                    const result = await fetch(`${origin}/api/extensions/${extension}/${route}`, { headers });
                    assert.equal(await result.text(), `${id}:${extension}:${route}`);
                }
            }
            const missing = await fetch(`${origin}/api/extensions/not-installed/client/Extension.dll`, { headers });
            assert.equal(missing.status, 404);
        }));
    }
    console.log('PASS: three simultaneous instance origins keep clients, inventories, assemblies, assets and credentials separate across repeated requests.');
} finally {
    for (const server of servers) server.closeAllConnections();
    await Promise.all(servers.map(server => new Promise(resolve => server.close(resolve))));
}

function listen(server) {
    return new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => resolve(server.address().port));
    });
}
