import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { openOnPort, ownedPorts, RANGE } = require('../main/ports');

// Which port an instance opens on decides which origin, and so which sign-in
// and which offline edits, its window sees. Driven here with a fake `start`:
// `held` is what something else on the machine is listening on.
const first = 41321;
let held = new Set();
const prepared = [];
const options = {
    first,
    start: async port => {
        if (held.has(port)) {
            throw Object.assign(new Error(`listen EADDRINUSE: address already in use 127.0.0.1:${port}`), { code: 'EADDRINUSE' });
        }
    },
    prepare: async port => prepared.push(port),
};

const a = { id: 'a', port: null, ports: [] };
const b = { id: 'b', port: null, ports: [] };
const instances = [a, b];

// New instances claim the first ports nobody has.
assert.deepEqual(await openOnPort(a, instances, options), { port: 41321, moved: false });
assert.deepEqual(await openOnPort(b, instances, options), { port: 41322, moved: false });

// Relaunching with nothing in the way changes nothing.
assert.deepEqual(await openOnPort(a, instances, options), { port: 41321, moved: false });
assert.deepEqual(a.ports, [41321]);

// Something else takes a's port: it moves to a new port of its own, past b's,
// and its storage there is cleared before first use.
held = new Set([41321]);
prepared.length = 0;
assert.deepEqual(await openOnPort(a, instances, options), { port: 41323, moved: true });
assert.deepEqual(a.ports, [41321, 41323]);
assert.deepEqual(prepared, [41323]);

// The next launch stays on the fallback, even with the main port free again:
// moving back would strand what was stored on the fallback's origin.
held = new Set();
assert.deepEqual(await openOnPort(a, instances, options), { port: 41323, moved: false });
assert.deepEqual(await openOnPort(a, instances, options), { port: 41323, moved: false });

// Only when the fallback is held does it move, to a port it already owns first.
held = new Set([41323]);
prepared.length = 0;
assert.deepEqual(await openOnPort(a, instances, options), { port: 41321, moved: true });
assert.deepEqual(prepared, [], 'a port the instance already owns keeps its storage');
assert.deepEqual(a.ports, [41321, 41323]);

// b never gets a port a owns, even with all of b's own held.
held = new Set([41322]);
assert.deepEqual(await openOnPort(b, instances, options), { port: 41324, moved: true });
const all = instances.flatMap(ownedPorts);
assert.equal(new Set(all).size, all.length, `no port belongs to two instances: ${all}`);

// Settings from before fallbacks have only `port`.
const legacy = { id: 'c', port: 41330 };
held = new Set();
assert.deepEqual(await openOnPort(legacy, [...instances, legacy], options), { port: 41330, moved: false });
assert.deepEqual(legacy.ports, [41330]);

// The range is the desktop's own and ends: with every port held, it fails
// rather than wander into the iOS app's (43321 up).
held = new Set(Array.from({ length: RANGE }, (_, i) => first + i));
await assert.rejects(openOnPort({ id: 'd', port: null, ports: [] }, instances, options), /no free local port from 41321 to 41420/);
assert.ok(first + RANGE <= 43321, 'clear of the iOS range');

// Anything but a held port stops at once instead of trying the next.
const failing = { ...options, start: async () => { throw Object.assign(new Error('permission denied'), { code: 'EACCES' }); } };
await assert.rejects(openOnPort({ id: 'e', port: 41340, ports: [41340] }, instances, failing), /permission denied/);

// Two windows open one instance at once, its last port held. Like main.js's
// ensureServer, the server is shared: a second start while one is starting
// gets the running server, whichever port it asked for. Both must end on the
// port the server really bound, and only one reports the move.
{
    held = new Set([41350]);
    const servers = new Map();
    // Clearing a new port's storage takes uneven time, which is what let two
    // opens pick different ports.
    let clears = 0;
    const shared = {
        first,
        prepare: () => new Promise(resolve => setTimeout(resolve, clears++ % 2 ? 0 : 30)),
        start: port => {
            let entry = servers.get('f');
            if (!entry) {
                const listening = new Promise((resolve, reject) => setTimeout(() =>
                    held.has(port)
                        ? reject(Object.assign(new Error('in use'), { code: 'EADDRINUSE' }))
                        : resolve(), 20));
                entry = { port, listening: listening.catch(err => { servers.delete('f'); throw err; }) };
                servers.set('f', entry);
            }
            const bound = entry.port;
            return entry.listening.then(() => bound);
        },
    };
    const f = { id: 'f', port: 41350, ports: [41350] };
    const others = [...instances, f];
    const [one, two] = await Promise.all([openOnPort(f, others, shared), openOnPort(f, others, shared)]);
    const listening = servers.get('f').port;
    assert.equal(one.port, listening);
    assert.equal(two.port, listening);
    assert.equal(f.port, listening, `saved ${f.port}, listening on ${listening}`);
    assert.equal([one, two].filter(r => r.moved).length, 1, 'one move reported');
    assert.ok(!instances.flatMap(ownedPorts).includes(listening), 'not a port another instance owns');
}

console.log('PASS: a held port moves the instance to a fallback of its own, relaunches stay there, ' +
    'no port is shared between instances, concurrent opens agree on the bound port, and the range stays clear of the iOS app.');
