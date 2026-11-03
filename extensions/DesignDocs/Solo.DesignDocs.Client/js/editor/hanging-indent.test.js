import test from 'node:test';
import assert from 'node:assert/strict';
import { EditorState } from '@codemirror/state';
import { markdown } from '@codemirror/lang-markdown';
import { hangingStart } from './hanging-indent.js';

const starts = doc => {
    const state = EditorState.create({ doc, extensions: [markdown()] });
    return Array.from({ length: state.doc.lines }, (_, i) => {
        const line = state.doc.line(i + 1);
        return hangingStart(state, line) - line.from;
    });
};

test('wrapped rows hang from the text after indentation and list markers', () => {
    assert.deepEqual(starts('plain\n\tindented\n    spaced'), [0, 1, 4]);
    assert.deepEqual(starts('- item\n    - child\n\n12. numbered'), [2, 6, 0, 4]);
});
test('quoted lines hang from the text after their quote markers and any list marker', () => {
    assert.deepEqual(starts('> quoted\n> > - nested item\n  lazy continuation'), [2, 6, 2]);
});
test('blank, marker-only and unlisted dash lines have nothing to hang from', () => {
    assert.deepEqual(starts('para\n    \n-\n\n---\n-not a list'), [0, 0, 0, 0, 0, 0]);
});
