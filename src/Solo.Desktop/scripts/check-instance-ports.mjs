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

console.log('PASS: a held port moves the instance to a fallback of its own, relaunches stay there, ' +
    'no port is shared between instances, and the range stays clear of the iOS app.');
