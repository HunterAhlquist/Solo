import test from 'node:test';
import assert from 'node:assert/strict';
import { EditorState } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { markdown } from '@codemirror/lang-markdown';
import { livePreview, taskToggle } from './preview.js';
import { markdownEditing } from './commands.js';

function widgets(doc, anchor = 0) {
    const field = livePreview({}, {});
    const state = EditorState.create({doc, selection:{anchor}, extensions:[markdown(), EditorView.editable.of(true), field]});
    const found = [];
    state.field(field).between(0, doc.length, (from, to, value) => {if(value.spec.widget)found.push({from,to,widget:value.spec.widget});});
    return found;
}
test('media stays rendered while the local cursor is on its line', () => {
    const doc='![[one.png]] ![[two.png]]';
    assert.equal(widgets(doc, 8).length, 1);
});
test('photos and sketches share a same-line stack', () => {
    assert.equal(widgets('![[one.png]] ![[Map.sketch]] ![[two.png]]').length, 1);
});
test('media on separate lines or beside prose is not collected into a carousel', () => {
    assert.equal(widgets('![[one.png]]\n![[two.png]]').length, 2);
    assert.equal(widgets('Before ![[one.png]] and ![[two.png]] after').length, 2);
});
test('embed examples in inline and fenced code are not rendered', () => {
    assert.equal(widgets('`![[one.png]]`\n\n```\n![[two.png]]\n```').length, 0);
});

test('rendered ATX headings hide the delimiter separator without indenting the title', () => {
    for(const heading of ['# hello','##   hello','###\thello']){
        const doc=heading+'\n\nbody',field=livePreview({},{}),state=EditorState.create({doc,selection:{anchor:doc.length},extensions:[markdown(),EditorView.editable.of(true),field]});
        const hidden=[];state.field(field).between(0,heading.length,(from,to,value)=>{if(value.isReplace&&!value.spec.widget)hidden.push([from,to]);});
        assert.deepEqual(hidden,[[0,heading.indexOf('hello')]],heading);
    }
});

test('list markers stay formatted until the caret directly touches their source characters',()=>{
    for(const doc of ['- list item','12. numbered item','- parent\n    - child']){
        const field=livePreview({},{}),make=anchor=>EditorState.create({doc,selection:{anchor},extensions:[markdown(),EditorView.editable.of(true),field]});
        const markers=state=>{const result=[];state.field(field).between(0,doc.length,(from,to,value)=>{if(value.spec.listMarker)result.push({from,to,raw:value.spec.widget.raw});});return result;};
        const atEnd=markers(make(doc.length));assert.ok(atEnd.length>0,doc);const marker=atEnd.at(-1);
        assert.equal(markers(make(marker.from)).length,atEnd.length-1);assert.equal(markers(make(marker.to)).length,atEnd.length-1);
        assert.equal(markers(make(marker.to+1)).length,atEnd.length);
    }
});
test('video embeds are never passed to the photo card/image loader',()=>{
    for(const doc of ['![[clip.mp4]]','![](media/demo.MOV)','![[clip.webm|640]]']){
        const found=widgets(doc,doc.length);assert.equal(found.filter(x=>x.widget.constructor.name==='MediaCard').length,0,doc);
    }
});
test('an unsupported video stays separate from neighboring supported photos',()=>{
    const doc='![[one.png]] ![[clip.mp4]] ![[two.png]]\n\nbody',found=widgets(doc,doc.length);
    assert.equal(found.filter(x=>x.widget.constructor.name==='MediaCard').length,2);assert.equal(found.filter(x=>x.widget.kind==='video').length,1);
});

test('document links stay rendered when the caret is elsewhere on the same line',()=>{
    for(const link of ['[[Movement]]','[Movement](Movement.md)','[Movement](/docs/note/AaFp_LuhfYeo9qSF7x1Gug)']){
        const doc='See '+link+' for details.';
        assert.equal(widgets(doc,doc.length).filter(x=>['WikiLink','Link'].includes(x.widget.constructor.name)).length,1);
        assert.equal(widgets(doc,6).filter(x=>['WikiLink','Link'].includes(x.widget.constructor.name)).length,0);
    }
});
test('note links support escaped title brackets and markdown paths containing spaces',()=>{
    const doc='See [Plan \\[v2\\]](/docs/note/AaFp_LuhfYeo9qSF7x1Gug) and [More](<More notes.md>) after.';
    const links=widgets(doc,doc.length).filter(x=>['WikiLink','Link'].includes(x.widget.constructor.name));
    assert.deepEqual(links.map(x=>x.widget.label),['Plan [v2]','More']);
});

test('highlight renders with its markup hidden off the caret line',()=>{
    const doc='==marked== text\n\nbody',field=livePreview({},{});
    const state=anchor=>EditorState.create({doc,selection:{anchor},extensions:[markdownEditing(),EditorView.editable.of(true),field]});
    const decorations=anchor=>{const marks=[],hidden=[];state(anchor).field(field).between(0,doc.length,(from,to,value)=>{if(value.spec.class)marks.push(value.spec.class);else if(value.isReplace&&!value.spec.widget)hidden.push(doc.slice(from,to));});return {marks,hidden};};
    assert.deepEqual(decorations(doc.length),{marks:['design-docs-highlight'],hidden:['==','==']});
    assert.deepEqual(decorations(0).hidden,[]);
});

test('inline markers hide when the cursor is elsewhere on the same line',()=>{
    const doc='**and** *having* ==specific== real-world',field=livePreview({},{});
    const hidden=anchor=>{const list=[];EditorState.create({doc,selection:{anchor},extensions:[markdownEditing(),EditorView.editable.of(true),field]}).field(field).between(0,doc.length,(from,to,value)=>{if(value.isReplace&&!value.spec.widget)list.push(doc.slice(from,to));});return list;};
    assert.deepEqual(hidden(doc.length-5),['**','**','*','*','==','==']);
    assert.deepEqual(hidden(doc.indexOf('having')),['**','**','==','==']);
    assert.deepEqual(hidden(doc.indexOf('**and**')+7),['*','*','==','==']);
});

test('block quotes draw one bar per nesting level and hide their markers off the caret line',()=>{
    const doc='> quote\nlazy\n\n> > nested\n\nbody',field=livePreview({},{});
    const decorate=anchor=>{
        const state=EditorState.create({doc,selection:{anchor},extensions:[markdown(),EditorView.editable.of(true),field]}),depths=[],hidden=[];
        state.field(field).between(0,doc.length,(from,to,value)=>{
            if(value.spec.class==='design-docs-quote')depths.push([state.doc.lineAt(from).number,value.spec.attributes.style]);
            else if(value.isReplace&&!value.spec.widget)hidden.push(doc.slice(from,to));
        });
        return {depths,hidden};
    };
    const away=decorate(doc.length);
    assert.deepEqual(away.depths,[[1,'--design-docs-quote-depth:1'],[2,'--design-docs-quote-depth:1'],[4,'--design-docs-quote-depth:2']]);
    assert.deepEqual(away.hidden,['> ','> ','> ']);
    assert.deepEqual(decorate(3).hidden,['> ','> '],'the caret line shows its marker');
});

// What is drawn in place of the text: each task box and list marker, with the source it stands for.
function drawn(doc,anchor){
    const field=livePreview({},{}),state=EditorState.create({doc,selection:{anchor},extensions:[markdownEditing(),EditorView.editable.of(true),field]});
    const boxes=[],markers=[];
    state.field(field).between(0,doc.length,(from,to,value)=>{
        if(value.spec.task)boxes.push([doc.slice(from,to),value.spec.widget.checked]);
        else if(value.spec.listMarker)markers.push(doc.slice(from,to));
    });
    return {boxes,markers};
}
test('a task is drawn as a box away from the caret',()=>{
    const doc='- [ ] buy milk\n- [x] feed cat\n* [X] water\n1. [X] numbered\n- [ ]not a task\n- [y] nor this\n\nbody';
    const away=drawn(doc,doc.length);
    // The box is the bullet: it stands for the list mark and the brackets. A numbered task keeps its number.
    assert.deepEqual(away.boxes,[['- [ ]',false],['- [x]',true],['* [X]',true],['[X]',true]]);
    assert.deepEqual(away.markers,['1.','-','-']);
});
test('a task is its text while the caret is in its box',()=>{
    const doc='- [ ] buy milk\n\n1. [x] numbered';
    // Anywhere from the list's mark to the end of the box: `- [ ]`, before the space after it.
    for(let caret=0;caret<=5;caret++){
        const on=drawn(doc,caret);
        assert.deepEqual(on.boxes,[['[x]',true]],`caret at ${caret}`);assert.deepEqual(on.markers,['1.'],`caret at ${caret}`);
    }
    // At the start of what the task says, and anywhere in it, the box is a box.
    for(const caret of [6,9,14])assert.equal(drawn(doc,caret).boxes.length,2,`caret at ${caret}`);
    const numbered=doc.indexOf('1.');
    for(let caret=numbered;caret<=numbered+6;caret++)assert.deepEqual(drawn(doc,caret),{boxes:[['- [ ]',false]],markers:[]},`caret at ${caret}`);
    assert.equal(drawn(doc,numbered+7).boxes.length,2);
});
test('a selection reaching into a task\'s box shows its text',()=>{
    const doc='- [ ] one\n- [ ] two',field=livePreview({},{});
    const state=EditorState.create({doc,selection:{anchor:8,head:doc.indexOf('two')-1},extensions:[markdownEditing(),EditorView.editable.of(true),field]});
    const boxes=[];state.field(field).between(0,doc.length,(from,_to,value)=>{if(value.spec.task)boxes.push(from);});
    assert.deepEqual(boxes,[0]);
});
test('task syntax in code is left as written',()=>{
    for(const doc of ['```\n- [ ] in a fence\n```\n\nbody','- `[ ] inline`\n\nbody','-     [ ] indented code\n\nbody','    - [ ] indented code\n\nbody'])
        assert.deepEqual(drawn(doc,doc.length).boxes,[],doc);
});
test('tasks are drawn inside quotes and nested lists',()=>{
    const doc='> - [ ] quoted\n\n- parent\n    - [x] child\n\nbody';
    assert.deepEqual(drawn(doc,doc.length).boxes,[['- [ ]',false],['- [x]',true]]);
});
test('a read-only note draws its tasks as boxes that are marked as not for ticking',()=>{
    const doc='- [ ] buy milk',field=livePreview({},{});
    const box=extensions=>{let found;EditorState.create({doc,extensions:[markdownEditing(),field,...extensions]}).field(field).between(0,doc.length,(_from,_to,value)=>{if(value.spec.task)found=value.spec.widget;});return found;};
    // Not editable, so the caret at the start doesn't turn the box back into text.
    assert.equal(box([EditorState.readOnly.of(true),EditorView.editable.of(false)]).locked,true);
    assert.equal(box([EditorView.editable.of(true)]),undefined);
});
test('ticking a task changes the one character in its brackets and leaves the caret alone',()=>{
    const doc='- [ ] buy milk\n* [x] feed cat\n12. [X] numbered\nplain';
    const state=EditorState.create({doc,selection:{anchor:doc.length},extensions:[markdownEditing()]});
    // A bulleted task's box starts at its list mark, a numbered one's at its bracket.
    const toggled=pos=>{const next=state.update(taskToggle(state,pos));assert.equal(next.state.selection.main.head,doc.length);assert.ok(next.isUserEvent('input'));return next.state.doc.toString();};
    assert.equal(toggled(0),doc.replace('- [ ]','- [x]'));
    assert.equal(toggled(doc.indexOf('*')),doc.replace('* [x]','* [ ]'));
    assert.equal(toggled(doc.indexOf('[X]')),doc.replace('[X]','[ ]'));
    // Ticking and unticking gets back what was written.
    const once=state.update(taskToggle(state,0)).state;assert.equal(once.update(taskToggle(once,0)).state.doc.toString(),doc);
    assert.equal(taskToggle(state,doc.indexOf('plain')),null);
    assert.equal(taskToggle(state,doc.indexOf('buy')),null);
});
test('a task in a read-only note is not ticked',()=>{
    const state=EditorState.create({doc:'- [ ] buy milk',extensions:[markdownEditing(),EditorState.readOnly.of(true)]});
    assert.equal(taskToggle(state,0),null);
});
