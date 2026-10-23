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
