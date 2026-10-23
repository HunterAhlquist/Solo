import test from 'node:test';
import assert from 'node:assert/strict';
import { EditorState } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { markdown } from '@codemirror/lang-markdown';
import { livePreview } from './preview.js';

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
