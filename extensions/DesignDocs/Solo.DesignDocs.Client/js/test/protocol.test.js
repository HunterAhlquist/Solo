import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { CHUNK_LENGTH, Kinds, PartAssembler, Reasons, frame, read, write } from '../src/sync/protocol.js';

const fixture = JSON.parse(readFileSync(new URL('./fixtures/protocol-examples.json', import.meta.url), 'utf8'));

test('the reasons and chunk length match the server', () => {
    assert.deepEqual(Object.values(Reasons).sort(), [...fixture.reasons].sort());
    assert.equal(CHUNK_LENGTH, fixture.chunkLength);
});

test('every example kind is a known kind', () => {
    for (const { kind } of fixture.examples) {
        assert.ok(Object.values(Kinds).includes(kind), kind);
    }
});

test('the client writes its messages as the examples show them', () => {
    const examples = fixture.examples.map(e => JSON.stringify(e.data));
    assert.ok(examples.includes(write.sync(0)));
    assert.ok(examples.includes(write.sync(1234567890123)));
    assert.ok(examples.includes(write.update('c1-7', 'AQLpqgEABAEEdGV4dAJoaQA=')));
    assert.ok(examples.includes(write.update('c1-8', 'AQLpqgEABAEEdGV4dAJoaQA=', '5b1f4c0e0b4a4a0d9d8b3c3c3a1f2e10')));
    assert.ok(examples.includes(write.compact(41, 'AQLpqgEABAEEdGV4dAJoaQA=')));
});

test('read gives null for nothing or garbage', () => {
    assert.equal(read(null), null);
    assert.equal(read(''), null);
    assert.equal(read('{nope'), null);
    assert.deepEqual(read('{"seq":4}'), { seq: 4 });
});

test('a short message goes whole', () => {
    assert.deepEqual(frame('update', 'abc'), [{ kind: 'update', data: 'abc' }]);
});

test('a long message goes in parts that reassemble in any order, once', () => {
    const data = 'x'.repeat(CHUNK_LENGTH * 2) + 'tail';
    const parts = frame('compact', data, () => 'm1');
    assert.equal(parts.length, 3);
    assert.ok(parts.every(p => p.kind === Kinds.part && p.data.length < 48000));

    const assembler = new PartAssembler();
    assert.equal(assembler.add(parts[2].data), null);
    assert.equal(assembler.add(parts[0].data), null);
    assert.equal(assembler.add(parts[0].data), null, 'a duplicate part changes nothing');
    assert.deepEqual(assembler.add(parts[1].data), { kind: 'compact', data });
    assert.equal(assembler.pending.size, 0);
});

test('parts refuse nesting, bad indexes and expire', () => {
    let now = 0;
    const assembler = new PartAssembler(1000, () => now);
    assert.equal(assembler.add(JSON.stringify({ k: 'part', id: 'a', i: 0, n: 1, d: '' })), null);
    assert.equal(assembler.add(JSON.stringify({ k: 'update', id: 'a', i: 2, n: 2, d: '' })), null);
    assert.equal(assembler.add('not json'), null);

    assert.equal(assembler.add(JSON.stringify({ k: 'update', id: 'b', i: 0, n: 2, d: 'ab' })), null);
    now = 2000;
    assert.equal(assembler.add(JSON.stringify({ k: 'update', id: 'b', i: 1, n: 2, d: 'cd' })), null,
        'the first half expired, so the second starts over');
});
