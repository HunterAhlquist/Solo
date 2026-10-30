import test from 'node:test';
import assert from 'node:assert/strict';
import {rangeAttributes,caretRange} from './text-format.js';

const plain=[{insert:'hello world'}];
const mixed=[{insert:'hello ',attributes:{bold:true}},{insert:'world'}];
const defaults={size:16,ink:'theme'};

test('an unformatted range reports the box defaults with everything off', () => {
    assert.deepEqual(rangeAttributes(plain,0,5,defaults),
        {bold:false,italic:false,underline:false,size:16,ink:'theme'});
});

test('a range inside one run reports that run', () => {
    assert.deepEqual(rangeAttributes(mixed,0,5,defaults),
        {bold:true,italic:false,underline:false,size:16,ink:'theme'});
});

// The case the panel exists to report honestly: half the selection is bold, so
// the B is neither lit nor unlit.
test('a range spanning differing runs reports null for what differs', () => {
    const attributes=rangeAttributes(mixed,0,11,defaults);

    assert.equal(attributes.bold,null);
    // Everything they agree on is still reported.
    assert.equal(attributes.italic,false);
    assert.equal(attributes.size,16);
    assert.equal(attributes.ink,'theme');
});

test('a run only touching the boundary is outside the range', () => {
    // [0,6) is exactly the bold run; the plain run starts at 6.
    assert.equal(rangeAttributes(mixed,0,6,defaults).bold,true);
    assert.equal(rangeAttributes(mixed,6,11,defaults).bold,false);
});

test('sizes and inks mix the same way', () => {
    const delta=[{insert:'a',attributes:{size:32,ink:'red'}},{insert:'b',attributes:{size:32,ink:'red'}},
        {insert:'c',attributes:{size:12,ink:'red'}}];

    assert.deepEqual(rangeAttributes(delta,0,2,defaults).size,32);
    assert.equal(rangeAttributes(delta,0,3,defaults).size,null);
    // Agreed-on ink survives a disagreement about size.
    assert.equal(rangeAttributes(delta,0,3,defaults).ink,'red');
});

test('a run inherits the box size and ink when it sets neither', () => {
    const delta=[{insert:'a',attributes:{bold:true}},{insert:'b'}];
    const attributes=rangeAttributes(delta,0,2,{size:24,ink:'blue'});

    assert.equal(attributes.size,24);
    assert.equal(attributes.ink,'blue');
    assert.equal(attributes.bold,null);
});

// A caret takes the format of the character behind it, so pressing B and typing
// continues in bold rather than resetting.
test('a caret reads the character before it', () => {
    assert.deepEqual(caretRange(3,3,11),[2,3]);
    assert.deepEqual(caretRange(0,0,11),[0,1]);
    assert.deepEqual(caretRange(0,0,0),[0,0]);
    assert.deepEqual(caretRange(2,7,11),[2,7]);

    // Caret just past the bold run: still bold.
    assert.equal(rangeAttributes(mixed,6,6,defaults).bold,true);
    // Caret one further in: now in the plain run.
    assert.equal(rangeAttributes(mixed,7,7,defaults).bold,false);
});

test('empty text reports the defaults rather than nothing', () => {
    assert.deepEqual(rangeAttributes([],0,0,defaults),
        {bold:false,italic:false,underline:false,size:16,ink:'theme'});
    assert.deepEqual(rangeAttributes([{insert:''}],0,0,defaults),
        {bold:false,italic:false,underline:false,size:16,ink:'theme'});
});
