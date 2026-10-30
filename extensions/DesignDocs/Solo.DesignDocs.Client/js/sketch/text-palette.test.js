import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

// What the formatting panel depends on that no unit can reach: the commands it
// sends, where it sits, and the editor styling it assumes. Each was wrong once
// and none failed loudly — the first silently moved the pen's colour, the
// second put the panel over the swatches it borrows.
const here=path.dirname(fileURLToPath(import.meta.url));
const read=file=>readFileSync(path.join(here,file),'utf8');
// The stylesheet sits beside the module directory, not inside it.
const css=()=>readFileSync(path.join(here,'..','design-docs.css'),'utf8');

// An ink click on a selection must name the colour, not clear it. Clearing
// makes the run inherit the box's ink — the pen colour the box was created
// with — so on a red box "theme" left the text red and theme was unreachable.
test('an ink command on a selection names the colour rather than clearing it', () => {
    const source=read('index.js');
    const line=source.split('\n').find(text=>text.includes("action==='ink'&&editing"));

    assert.ok(line,'the ink command must still special-case an open text editor');
    assert.match(line,/formatSelection\(\{ink:value\}\)/);
    assert.doesNotMatch(line,/value==='theme'\?null/,
        'clearing the attribute makes the run inherit the box ink, so theme becomes unreachable');
});

// The panel follows the box now, so the two halves of that have to stay
// connected: the stylesheet positions it from custom properties, and only the
// sketch sets them. Either one alone leaves the panel parked at its fallback.
test('the formatting palette is positioned from the properties the sketch sets', () => {
    const declarations=[...css().matchAll(/\.design-docs-sketch-text-palette \{([^}]*)\}/g)].map(match=>match[1]);
    assert.equal(declarations.length,1,'one rule, at every width — it follows the box, not the layout');

    const [rule]=declarations;
    for(const axis of ['x','y'])
        assert.match(rule,new RegExp(`var\\(--design-docs-text-panel-${axis},`),
            `the ${axis} position must come from the property, with a fallback for the first frame`);

    const source=read('index.js');
    for(const axis of ['x','y'])
        assert.ok(source.includes(`'--design-docs-text-panel-${axis}'`),
            `the sketch must set --design-docs-text-panel-${axis}`);
});

// Hiding the textarea's own caret and highlight is half of drawing them on the
// canvas; without the other half the two are drawn on top of each other, which
// is what the mismatch looked like to begin with.
test('the editor hides the caret and highlight the canvas draws', () => {
    const style=css();

    assert.match(style,/\.design-docs-sketch-text \{[^}]*caret-color:transparent/);
    assert.match(style,/\.design-docs-sketch-text::selection \{[^}]*background:transparent/);
    assert.ok(read('index.js').includes('selectionRects('),
        'the sketch must draw the selection it just hid');
});

// The editor's gestures are the other half of drawing the caret ourselves, and
// each one that is inlined back into index.js loses its unit test with it.
test('the editor reads its click gestures from the caret module', () => {
    const source=read('index.js');

    for(const name of ['offsetAt(','shiftAnchor(','wordAt('])
        assert.ok(source.includes(name),`the editor must place a click with ${name.slice(0,-1)}`);
    assert.doesNotMatch(source,/index<input\.selectionStart/,
        'the shift-click anchor comes from the selection direction, not from the click');
});
