import test from 'node:test';
import assert from 'node:assert/strict';
import {textLayout,textRuns,runFont,runSize,textSize,uniformFace} from './font.js';

// Width is proportional to length so positions are predictable; the real
// measurement comes from the canvas.
const context=()=>({font:'',textBaseline:'',fillStyle:'',measureText:text=>({width:text.length*10})});

test('a box with no runs lays out from its plain string', () => {
    assert.deepEqual(textRuns({text:'hi'}), [{text:'hi'}]);
    assert.deepEqual(textRuns({text:'',runs:[]}), [{text:''}]);
});

// The geometry this file had before runs existed. An existing sketch has to
// render identically, so this is the case worth pinning hardest: one line,
// height 1.5x the size, the glyph top a quarter of a size down.
test('an unformatted box reduces to the original single-run geometry', () => {
    const layout=textLayout(context(),{text:'abc',fontSize:16,x:0,y:0},'Inter');

    assert.equal(layout.lines.length,1);
    assert.equal(layout.height,24);
    assert.equal(layout.width,30);
    const [run]=layout.lines[0].runs;
    assert.equal(run.x,0);
    assert.equal(run.top,4);
    assert.equal(run.size,16);
});

test('each newline starts a line, and a trailing one still takes height', () => {
    const layout=textLayout(context(),{text:'a\nbb\n',fontSize:16},'Inter');

    assert.equal(layout.lines.length,3);
    assert.deepEqual(layout.lines.map(line=>line.top),[0,24,48]);
    assert.equal(layout.lines[2].runs.length,0);
    assert.equal(layout.height,72);
    // The widest line decides the box.
    assert.equal(layout.width,20);
});

test('a newline inside a formatted run still breaks the line', () => {
    const layout=textLayout(context(),{
        runs:[{insert:'a\nb',attributes:{bold:true}}],fontSize:16,
    },'Inter');

    assert.equal(layout.lines.length,2);
    assert.equal(layout.lines[0].runs[0].bold,true);
    assert.equal(layout.lines[1].runs[0].bold,true);
});

test('runs sit side by side and advance across the line', () => {
    const layout=textLayout(context(),{
        runs:[{insert:'ab'},{insert:'cd',attributes:{bold:true}}],fontSize:16,
    },'Inter');

    const [first,second]=layout.lines[0].runs;
    assert.equal(first.x,0);
    assert.equal(second.x,20);
    assert.equal(layout.lines[0].width,40);
    assert.equal(second.bold,true);
});

// A line takes its height from its tallest run, and shorter runs drop so their
// bottoms line up rather than their tops — which is what reads as a baseline.
test('a mixed-size line is sized by its tallest run and bottom-aligned', () => {
    const layout=textLayout(context(),{
        runs:[{insert:'big',attributes:{size:32}},{insert:'small',attributes:{size:16}}],fontSize:16,
    },'Inter');

    const line=layout.lines[0];
    assert.equal(line.size,32);
    assert.equal(line.height,48);

    const [big,small]=line.runs;
    assert.equal(big.top,8);
    // 8 of line padding, then dropped by the 16px size difference.
    assert.equal(small.top,24);
    assert.equal(big.top+big.size,small.top+small.size);
});

test('a run without a size inherits the box, and sizes are clamped', () => {
    assert.equal(runSize({},16),16);
    assert.equal(runSize({size:40},16),40);
    assert.equal(runSize({size:1},16),8);
    assert.equal(runSize({size:999},16),64);
    // The box's own size comes from fontSize, or the legacy scale.
    assert.equal(textSize({fontSize:20}),20);
    assert.equal(textSize({scale:3}),24);
    assert.equal(textSize({}),16);
});

test('the font string carries weight and slant', () => {
    assert.equal(runFont({size:16},'Inter'),'16px Inter');
    assert.equal(runFont({size:16,bold:true},'Inter'),'700 16px Inter');
    assert.equal(runFont({size:16,italic:true},'Inter'),'italic 16px Inter');
    assert.equal(runFont({size:16,bold:true,italic:true},'Inter'),'italic 700 16px Inter');
    // Underline is drawn, not a font property, so it must not appear here.
    assert.equal(runFont({size:16,underline:true},'Inter'),'16px Inter');
});

test('the layout can give the plain text back', () => {
    const layout=textLayout(context(),{runs:[{insert:'ab'},{insert:'c\nd'}]},'Inter');
    assert.equal(layout.text,'abc\nd');
});

// The editor turns a caret index into a position, so every placed run has to
// know where it starts in the string — including across the newlines that split
// one run into several, which is where an off-by-one hides.
test('runs and lines carry their character offsets', () => {
    const layout=textLayout(context(),{text:'',runs:[{insert:'ab\ncd'},{insert:'ef'}]},'Inter');

    assert.deepEqual(layout.lines.map(line=>[line.start,line.end]),[[0,2],[3,7]]);
    assert.deepEqual(layout.lines[1].runs.map(run=>run.index),[3,5]);
});

// The overlay textarea can only have one font. Where the runs agree it gets
// theirs, so its own hit testing still lands on the right character.
test('a uniform face is reported, and a mixed one is not', () => {
    const face=size=>uniformFace(textLayout(context(),{text:'ab',fontSize:size},'Inter'));

    assert.deepEqual(face(24),{size:24,bold:false,italic:false});
    assert.equal(uniformFace(textLayout(context(),
        {text:'',runs:[{insert:'a',attributes:{size:32}},{insert:'b'}]},'Inter')),null);
    assert.equal(uniformFace(textLayout(context(),
        {text:'',runs:[{insert:'a',attributes:{bold:true}},{insert:'b'}]},'Inter')),null);
    // Nothing typed yet: no runs to disagree, and the box default stands.
    assert.equal(uniformFace(textLayout(context(),{text:''},'Inter')),null);
});
