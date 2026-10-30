import test from 'node:test';
import assert from 'node:assert/strict';
import {textLayout} from './font.js';
import {caretAt,lineAt,offsetAt,selectionRects,shiftAnchor,wordAt} from './text-caret.js';

// Width proportional to length, as in font.test.js, but scaled by the run's
// size so a bigger run really is wider — which is the whole case these guard.
const context=()=>({font:'',measureText(text){
    const size=Number(/(\d+)px/.exec(this.font)?.[1]??16);
    return {width:text.length*size/2};
}});
const layout=box=>textLayout(context(),box,'Inter');
const at=(box,index)=>caretAt(context(),layout(box),'Inter',index);
const rects=(box,from,to)=>selectionRects(context(),layout(box),'Inter',from,to);
const hit=(box,x,y)=>offsetAt(context(),layout(box),'Inter',x,y);

const plain={text:'abcd',fontSize:16,x:0,y:0};
// "ab" at 32px then "cd" at 16px, which is the shape in the report: the band
// kept the small size while the glyphs were drawn large.
const mixed={text:'abcd',fontSize:16,x:0,y:0,
    runs:[{insert:'ab',attributes:{size:32}},{insert:'cd'}]};

test('the caret is the line box of the run it would type into', () => {
    // Unformatted: 16px text, so a 24px caret at the top of the line.
    assert.deepEqual(at(plain,0),{x:0,y:0,height:24});
    assert.deepEqual(at(plain,2),{x:16,y:0,height:24});

    // Inside the 32px run the caret is 48px; after it, the 16px run's 24px.
    assert.equal(at(mixed,1).height,48);
    assert.equal(at(mixed,4).height,24);
});

// The bug as reported: the highlight has to grow with the text, not with the
// editor's own font. The line is 32px tall at its largest run, so 48px of band.
test('the selection band follows the size of the text, not the box default', () => {
    assert.deepEqual(rects(plain,0,4),[{x:0,y:0,width:32,height:24}]);

    const [band]=rects(mixed,0,4);
    assert.equal(band.height,48,'a 32px run makes a 48px line');
    assert.equal(band.y,0);
    // 2 characters at 32px plus 2 at 16px.
    assert.equal(band.width,32+16);
});

test('a selection across lines is one band per line, and shows the break', () => {
    const box={text:'ab\ncd',fontSize:16,x:0,y:0};
    const [first,second]=rects(box,1,4);

    assert.equal(first.y,0);
    assert.equal(second.y,24);
    // The first band runs past the last glyph because the newline is selected.
    assert.ok(first.width>8,'the break is shown as a stub past the text');
    assert.equal(second.x,0);
    assert.equal(second.width,8);
});

test('an empty selection has no band', () => {
    assert.deepEqual(rects(plain,2,2),[]);
});

// What the textarea gets wrong on its own: it measures with one font, so in
// mixed text the character it picks is not the one under the pointer.
test('a click lands on the character under it, at any run size', () => {
    // 'ab' spans 0..32 at 32px; the boundary between a and b is at 16.
    assert.equal(hit(mixed,0,10),0);
    assert.equal(hit(mixed,15,10),1);
    assert.equal(hit(mixed,33,10),2);
    // Past the end of the line clamps to its end rather than wrapping.
    assert.equal(hit(mixed,500,10),4);
});

test('a click picks the nearer boundary, not the character it is inside', () => {
    // 8px per character; 5 is past the midpoint of the first.
    assert.equal(hit(plain,3,10),0);
    assert.equal(hit(plain,5,10),1);
});

test('a click below the last line stays on it', () => {
    const box={text:'ab\ncd',fontSize:16,x:0,y:0};
    assert.equal(hit(box,0,9999),3,'the start of the second line');
});

test('an index at the end of a line belongs to that line', () => {
    const box={text:'ab\ncd',fontSize:16,x:0,y:0};
    assert.equal(lineAt(layout(box),2).top,0);
    assert.equal(lineAt(layout(box),3).top,24);
});

test('a double click takes the word, and whitespace takes nothing', () => {
    assert.deepEqual(wordAt('one two',5),[4,7]);
    assert.deepEqual(wordAt('one two',1),[0,3]);
    assert.deepEqual(wordAt('one two',3),[0,3]);
    assert.deepEqual(wordAt('one  two',4),[4,4]);
});

// Reading the anchor off the click instead of off the selection moves whichever
// end is nearer, so shift-clicking inside a backward range collapses it from the
// wrong side — and the next keystroke or format lands on the wrong half.
test('a shift-click keeps the end the selection is anchored to', () => {
    assert.equal(shiftAnchor({selectionStart:2,selectionEnd:8,selectionDirection:'backward'}),8);
    assert.equal(shiftAnchor({selectionStart:2,selectionEnd:8,selectionDirection:'forward'}),2);
    // A caret has no direction to read, and either end is the same place.
    assert.equal(shiftAnchor({selectionStart:5,selectionEnd:5,selectionDirection:'none'}),5);
});
