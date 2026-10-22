import test from 'node:test';
import assert from 'node:assert/strict';
import * as Y from 'yjs';
import { encode, decode, snapshot, apply, pressureWidth, stampOffset } from './prototype-protocol.js';

test('three peers converge after duplicate, reordered updates and a disconnected edit', () => {
    const peers = [new Y.Doc(), new Y.Doc(), new Y.Doc()];
    const updates = [];
    for (const [i, peer] of peers.entries()) {
        peer.on('update', (bytes, origin) => { if (origin !== 'relay') updates.push(encode(bytes)); });
        peer.getText('markdown').insert(0, `writer ${i}\n`);
    }
    for (const peer of peers) for (const update of [...updates].reverse()) apply(peer, update, 'relay');
    for (const peer of peers) for (const update of updates) apply(peer, update, 'relay');
    peers[0].getText('markdown').insert(0, 'during the gap\n');
    for (const peer of peers.slice(1)) apply(peer, snapshot(peers[0]), 'relay');
    assert.equal(new Set(peers.map(p => p.getText('markdown').toString())).size, 1);
    for (const peer of peers) peer.destroy();
});

test('wire encoding preserves every byte', () => {
    const bytes = Uint8Array.from({ length: 256 }, (_, i) => i);
    assert.deepEqual(decode(encode(bytes)), bytes);
});

test('Pencil pressure rises from the selected minimum; mouse pressure is ignored', () => {
    assert.equal(pressureWidth(0, 0, 'pen'), 1);
    assert.equal(pressureWidth(0, 1, 'pen'), 8);
    assert.equal(pressureWidth(3, 0, 'pen'), 5);
    assert.equal(pressureWidth(3, 0.5, 'mouse'), 5);
    assert.equal(pressureWidth(4, 1, 'pen'), 8);
});

test('stamp boil is deterministic and never exceeds a paint pixel', () => {
    for (let i = 0; i < 100; i++) for (let frame = 0; frame < 3; frame++) {
        const offset = stampOffset(42, i, frame);
        assert.deepEqual(offset, stampOffset(42, i, frame));
        assert.ok(offset.every(n => n >= -1 && n <= 1));
    }
});
