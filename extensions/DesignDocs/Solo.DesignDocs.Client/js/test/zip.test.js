import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as Y from 'yjs';
import { batches, readVaultZip, rewriteSketchEmbeds, textFromLog, writeZip } from '../src/zip/obsidianZip.js';
import { toBase64 } from '../src/sync/base64.js';

const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1, 2, 3]);

test('a vault zip sorts into notes, embedded images and skipped files', () => {
    const zip = writeZip({
        'My Vault/Home.md': '﻿# Home\r\n![[hero.png]]\r\n![map](Design/arena%20map.png)\r\n',
        'My Vault/Design/Combat.md': 'See [[Home]]',
        'My Vault/Design/arena map.png': png,
        'My Vault/attachments/hero.png': png,
        'My Vault/attachments/unused.png': png,
        'My Vault/Board.canvas': '{}',
        'My Vault/Sketch.excalidraw.md': '',
        'My Vault/.obsidian/app.json': '{}',
        'My Vault/notes.pdf': png,
    });

    const vault = readVaultZip(zip);
    assert.deepEqual(vault.notes.map(n => n.path).sort(), ['Design/Combat.md', 'Home.md']);
    assert.ok(vault.notes.find(n => n.path === 'Home.md').markdown.startsWith('﻿# Home\r\n'),
        'the text comes in byte for byte');
    assert.deepEqual(vault.images.map(i => i.path).sort(), ['Design/arena map.png', 'attachments/hero.png']);
    assert.deepEqual(vault.folders, ['Design']);
    const skipped = Object.fromEntries(vault.skipped.map(s => [s.path, s.reason]));
    assert.match(skipped['attachments/unused.png'], /No note embeds it/);
    assert.match(skipped['Board.canvas'], /Canvas/);
    assert.match(skipped['Sketch.excalidraw.md'], /Excalidraw/);
    assert.match(skipped['notes.pdf'], /Not a note/);
    assert.equal(skipped['.obsidian/app.json'], undefined, 'Obsidian\'s settings go unmentioned');
});

test('images embedded relative to a note\'s folder are found, not skipped', () => {
    const zip = writeZip({
        'Vault/Design/Combat/Dodge.md': '![](../../attachments/roll.png)\n![[./arc.png]]',
        'Vault/attachments/roll.png': png,
        'Vault/Design/Combat/arc.png': png,
        'Vault/arc.png': png,
    });

    const vault = readVaultZip(zip);
    assert.deepEqual(vault.images.map(i => i.path).sort(), ['Design/Combat/arc.png', 'attachments/roll.png']);
    assert.match(vault.skipped.find(s => s.path === 'arc.png').reason, /No note embeds it/);
});

test('notes batch under the per-call limits', () => {
    const notes = Array.from({ length: 250 }, (_, i) => ({ path: `${i}.md`, markdown: 'x' }));
    assert.deepEqual(batches(notes).map(b => b.length), [100, 100, 50]);
    const big = [{ path: 'a.md', markdown: 'x'.repeat(4) }, { path: 'b.md', markdown: 'x'.repeat(4) }];
    assert.deepEqual(batches(big, 100, 6).map(b => b.length), [1, 1]);
});

test('an exported note is its log\'s text, or its seed when nobody opened it', () => {
    const doc = new Y.Doc();
    doc.getText('text').insert(0, '# Hello\n\nWorld');
    const log = { entries: [{ seq: 1, isSnapshot: false, data: toBase64(Y.encodeStateAsUpdate(doc)) }] };
    assert.equal(textFromLog(log), '# Hello\n\nWorld');
    assert.equal(textFromLog({ entries: [], seedMarkdown: 'Seed\r\n' }), 'Seed\r\n');
});

test('sketch embeds point at their PNGs in an exported copy', () => {
    assert.equal(rewriteSketchEmbeds('a ![[Roll arc.sketch]] b ![[x.png]] ![[Arc.sketch|300]]'),
        'a ![[Roll arc.sketch.png]] b ![[x.png]] ![[Arc.sketch.png|300]]');
});
