import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as Y from 'yjs';
import { SyncProvider } from '../src/sync/provider.js';
import { seedNote, TEXT } from '../src/editor/note.js';

// A relay that behaves as Live/DocumentSyncHandler.cs does, over a hub
// connection per client: in order within a connection, as SignalR is, but
// interleaved at random across connections. Losses happen the way they do
// for real, as a connection dropping with whatever it was carrying.
class FakeRelay {
    constructor(random, { seed = null } = {}) {
        this.random = random;
        this.log = [];
        this.seed = seed;
        this.lease = null;
        this.clients = new Set();
        this.channels = new Map();
    }

    connect(client) {
        this.clients.add(client);
        this.channels.set(client, { up: [], down: [] });
    }

    get inFlight() {
        let count = 0;
        for (const { up, down } of this.channels.values()) {
            count += up.length + down.length;
        }

        return count;
    }

    /** What the client's send() does: the hub takes it while the connection is up. */
    send(client, kind, data) {
        if (!client.online) {
            return Promise.resolve('Failed');
        }

        this.channels.get(client).up.push({ client, kind, data });
        return Promise.resolve('Accepted');
    }

    toClient(client, kind, data) {
        if (client.online) {
            this.channels.get(client).down.push({ client, kind, data });
        }
    }

    /** The connection drops: everything on it is lost. */
    drop(client) {
        client.online = false;
        client.provider.setConnected(false);
        this.channels.set(client, { up: [], down: [] });
    }

    handle({ client, kind, data }) {
        const message = data ? JSON.parse(data) : null;
        switch (kind) {
            case 'sync': {
                if (this.seed !== null && this.log.length === 0) {
                    if (this.lease && this.lease.client !== client) {
                        this.toClient(client, 'seeding', null);
                    } else {
                        this.lease = { client, id: `lease-${client.name}` };
                        this.toClient(client, 'seed', JSON.stringify({ markdown: this.seed, lease: this.lease.id }));
                    }

                    return;
                }

                const entries = this.log.filter(e => e.seq > message.afterSeq).map(e => [e.seq, e.data]);
                this.toClient(client, 'state', JSON.stringify({ entries }));
                this.toClient(client, 'synced', JSON.stringify({ seq: this.log.at(-1)?.seq ?? 0, pending: this.log.length }));
                return;
            }

            case 'update': {
                if (this.seed !== null && this.log.length === 0) {
                    if (message.lease !== this.lease?.id) {
                        this.toClient(client, 'refused', JSON.stringify({ id: message.id, reason: 'seed-lost' }));
                        return;
                    }

                    this.seed = null;
                    this.lease = null;
                } else if (message.lease) {
                    // A seed for a note someone already seeded: it would double the text.
                    this.toClient(client, 'refused', JSON.stringify({ id: message.id, reason: 'seed-lost' }));
                    return;
                }

                // Seqs are shared by every document, so one document's have gaps.
                const seq = (this.log.at(-1)?.seq ?? 0) + 1 + Math.floor(this.random() * 3);
                this.log.push({ seq, data: message.data });
                this.toClient(client, 'ack', JSON.stringify({ id: message.id, seq }));
                for (const other of this.clients) {
                    if (other !== client) {
                        this.toClient(other, 'update', JSON.stringify({ seq, data: message.data }));
                    }
                }

                return;
            }
        }
    }

    /** Delivers the next message on one connection, picked at random. */
    step() {
        const busy = [...this.channels.entries()].flatMap(([client, { up, down }]) =>
            [up.length > 0 ? [client, up, true] : null, down.length > 0 ? [client, down, false] : null].filter(Boolean));
        if (busy.length === 0) {
            return false;
        }

        const [client, queue, toServer] = busy[Math.floor(this.random() * busy.length)];
        const message = queue.shift();
        if (toServer) {
            this.handle(message);
        } else {
            client.provider.receive(message.kind, message.data);
        }

        return true;
    }
}

class Timers {
    constructor() {
        this.queue = new Map();
        this.next = 1;
    }

    set = (f, ms) => {
        const id = this.next++;
        this.queue.set(id, f);
        return id;
    };

    clear = id => this.queue.delete(id);

    runAll() {
        const due = [...this.queue.values()];
        this.queue.clear();
        due.forEach(f => f());
        return due.length > 0;
    }
}

function mulberry32(seed) {
    return () => {
        seed = (seed + 0x6d2b79f5) | 0;
        let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

function client(relay, name, canWrite = true, options = {}) {
    const doc = new Y.Doc();
    const timers = new Timers();
    const c = { name, doc, timers, online: true };
    c.provider = new SyncProvider(doc, {
        send: (kind, data) => relay.send(c, kind, data),
        canWrite,
        timers,
        compactAfter: 1e9,
        ...options,
    });
    relay.connect(c);
    return c;
}

const microtasks = () => new Promise(resolve => setImmediate(resolve));

async function settle(relay, clients) {
    for (let round = 0; round < 10000; round++) {
        await microtasks();
        const moved = relay.step();
        const fired = clients.map(c => c.timers.runAll()).some(Boolean);
        if (!moved && !fired) {
            await microtasks();
            if (relay.inFlight === 0 && !clients.some(c => c.timers.queue.size > 0)) {
                return;
            }
        }
    }

    throw new Error('the network never settled');
}

const text = c => c.doc.getText(TEXT).toString();

test('writers and a reader converge through dropped connections and interleaving', async () => {
    for (const seed of [1, 2, 3, 4, 5, 6, 7, 8]) {
        const random = mulberry32(seed);
        const relay = new FakeRelay(random);
        const writers = [client(relay, 'a'), client(relay, 'b'), client(relay, 'c')];
        const reader = client(relay, 'r', false);
        const everyone = [...writers, reader];
        everyone.forEach(c => c.provider.start());
        await settle(relay, everyone);

        for (let i = 0; i < 150; i++) {
            const writer = writers[Math.floor(random() * writers.length)];
            const ytext = writer.doc.getText(TEXT);
            const at = Math.floor(random() * (ytext.length + 1));
            if (ytext.length > 5 && random() < 0.3) {
                ytext.delete(Math.min(at, ytext.length - 1), 1);
            } else {
                ytext.insert(at, String.fromCharCode(97 + Math.floor(random() * 26)));
            }

            const roll = random();
            const someone = everyone[Math.floor(random() * everyone.length)];
            if (roll < 0.04 && someone.online) {
                relay.drop(someone);
            } else if (roll < 0.12 && !someone.online) {
                // Back online: the topic is live again a moment before the
                // page hears about it, so relays can arrive before the resync.
                someone.online = true;
                relay.step();
                relay.step();
                someone.provider.setConnected(true);
            }

            for (let s = 0; s < 3; s++) {
                relay.step();
            }

            await microtasks();
            writer.timers.runAll();
        }

        for (const c of everyone.filter(c => !c.online)) {
            c.online = true;
            c.provider.setConnected(true);
        }

        await settle(relay, everyone);

        const expected = text(writers[0]);
        assert.ok(expected.length > 0);
        for (const c of everyone) {
            assert.equal(text(c), expected, `seed ${seed}: ${c.name} differs`);
            assert.equal(c.provider.unacked, 0, `seed ${seed}: ${c.name} has unacked updates`);
        }

        const fromLog = new Y.Doc();
        relay.log.forEach(e => Y.applyUpdate(fromLog, Buffer.from(e.data, 'base64')));
        assert.equal(fromLog.getText(TEXT).toString(), expected, `seed ${seed}: the log differs`);
    }
});

test('an update relayed before the resync does not hide what was missed offline', async () => {
    const relay = new FakeRelay(mulberry32(11));
    const a = client(relay, 'a');
    const b = client(relay, 'b');
    [a, b].forEach(c => c.provider.start());
    await settle(relay, [a, b]);

    relay.drop(b);
    a.doc.getText(TEXT).insert(0, 'missed ');
    await settle(relay, [a, b]);

    b.online = true;
    a.doc.getText(TEXT).insert(7, 'relayed');
    await settle(relay, [a, b]);
    assert.equal(text(b), '', 'the second update waits on the first');

    b.provider.setConnected(true);
    await settle(relay, [a, b]);
    assert.equal(text(b), 'missed relayed');
});

test('a reader sends nothing', async () => {
    const relay = new FakeRelay(mulberry32(9));
    const reader = client(relay, 'r', false);
    reader.provider.start();
    await settle(relay, [reader]);
    reader.doc.getText(TEXT).insert(0, 'local only');
    await settle(relay, [reader]);
    assert.equal(relay.log.length, 0);
});

test('two writers opening an imported note seed it exactly once', async () => {
    const markdown = '# Imported\r\n\r\nText';
    const relay = new FakeRelay(mulberry32(7), { seed: markdown });
    const a = client(relay, 'a', true, { seed: seedNote });
    const b = client(relay, 'b', true, { seed: seedNote });
    a.provider.start();
    b.provider.start();
    await settle(relay, [a, b]);

    assert.equal(relay.log.length, 1);
    assert.equal(text(a), '# Imported\n\nText');
    assert.equal(text(b), '# Imported\n\nText');
});

test('a refusal for a read-only writer resets the document', async () => {
    const relay = new FakeRelay(mulberry32(3));
    const writer = client(relay, 'w');
    const resets = [];
    writer.provider.on.reset = reason => resets.push(reason);
    relay.handle = function ({ client: from, kind, data }) {
        if (kind === 'update') {
            this.toClient(from, 'refused', JSON.stringify({ id: JSON.parse(data).id, reason: 'read-only' }));
        } else {
            FakeRelay.prototype.handle.call(this, { client: from, kind, data });
        }
    };
    writer.provider.start();
    await settle(relay, [writer]);
    writer.doc.getText(TEXT).insert(0, 'no');
    await settle(relay, [writer]);

    assert.deepEqual(resets, ['read-only']);
    assert.equal(writer.provider.canWrite, false);
    assert.equal(writer.provider.unacked, 0);
});
