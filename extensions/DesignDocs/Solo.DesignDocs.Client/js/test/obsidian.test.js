import { test } from 'node:test';
import assert from 'node:assert/strict';
import { VaultIndex, parseWikiLink } from '../src/links/obsidian.js';

const tree = {
    folders: [
        { id: 'f1', parentId: null, name: 'Design' },
        { id: 'f2', parentId: 'f1', name: 'Combat' },
        { id: 'f3', parentId: null, name: 'Story' },
    ],
    notes: [
        { id: 'n1', folderId: null, name: 'Home' },
        { id: 'n2', folderId: 'f2', name: 'Dodge' },
        { id: 'n3', folderId: 'f1', name: 'Ideas' },
        { id: 'n4', folderId: 'f3', name: 'Ideas' },
    ],
    images: [
        { id: 'i1', path: 'attachments/hero.png' },
        { id: 'i2', path: 'Design/Combat/arena map.webp' },
        { id: 'i3', path: 'a/same.png' },
        { id: 'i4', path: 'b/same.png' },
    ],
    sketches: [{ id: 's1', noteId: 'n2', name: 'Roll arc' }],
};

test('wiki links split into target, heading and alias', () => {
    assert.deepEqual(parseWikiLink('Note#Part|Shown'), { target: 'Note', heading: 'Part', alias: 'Shown' });
    assert.deepEqual(parseWikiLink('#Only heading'), { target: '', heading: 'Only heading', alias: null });
    assert.deepEqual(parseWikiLink(' Note '), { target: 'Note', heading: null, alias: null });
});

test('notes resolve by full path, unique name, then the linking note\'s folder', () => {
    const index = new VaultIndex(tree);
    assert.equal(index.notesById.get('n2').path, 'Design/Combat/Dodge');
    assert.equal(index.resolveNote('Design/Combat/Dodge')?.id, 'n2');
    assert.equal(index.resolveNote('dodge.md')?.id, 'n2');
    assert.equal(index.resolveNote('Combat/Dodge')?.id, 'n2');
    assert.equal(index.resolveNote('Ideas'), null, 'two notes share the name');
    assert.equal(index.resolveNote('Ideas', 'n3')?.id, 'n3');
    assert.equal(index.resolveNote('Story/Ideas')?.id, 'n4');
    assert.equal(index.resolveNote('', 'n1')?.id, 'n1', 'a heading-only link is the note itself');
    assert.equal(index.resolveNote('Missing'), null);
});

test('images resolve by path or unique file name, decoding markdown links', () => {
    const index = new VaultIndex(tree);
    assert.equal(index.resolveImage('hero.png')?.id, 'i1');
    assert.equal(index.resolveImage('Design/Combat/arena%20map.webp', true)?.id, 'i2');
    assert.equal(index.resolveImage('same.png'), null);
    assert.equal(index.resolveImage('b/same.png')?.id, 'i4');
});

test('sketches resolve by name and new images get free paths', () => {
    const index = new VaultIndex(tree);
    assert.equal(index.resolveSketch('roll arc.sketch')?.id, 's1');
    assert.equal(index.freeImagePath('hero.png'), 'attachments/hero 2.png');
    assert.equal(index.freeImagePath('a:b?.png'), 'attachments/a-b-.png');
});
