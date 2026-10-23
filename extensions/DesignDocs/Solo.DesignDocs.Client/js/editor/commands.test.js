import test from 'node:test';import assert from 'node:assert/strict';
import {EditorState} from '@codemirror/state';import {keymap} from '@codemirror/view';import {syntaxTree} from '@codemirror/language';import {markdownEditing} from './commands.js';
function editor(doc){const view={state:EditorState.create({doc,selection:{anchor:doc.length},extensions:markdownEditing()}),dispatch:transaction=>{view.state=transaction.state;}};return view;}
function press(view,key,shift=false){for(const bindings of view.state.facet(keymap))for(const binding of bindings)if(binding.key===key){const run=shift?binding.shift:binding.run;if(run?.(view))return;}throw new Error('Unhandled '+key);}
test('Return continues bullets and Enter on an empty item exits the list',()=>{
    const view=editor('- first');press(view,'Enter');assert.equal(view.state.doc.toString(),'- first\n- ');press(view,'Enter');assert.equal(view.state.doc.toString(),'- first\n');
});
test('Return continues ordered numbering and renumbers the following items',()=>{
    const view=editor('1. first\n2. second');view.state=view.state.update({selection:{anchor:8}}).state;press(view,'Enter');assert.equal(view.state.doc.toString(),'1. first\n2. \n3. second');
});
test('Tab nests bullets and numbers; Return keeps the nested level; Shift-Tab dedents',()=>{
    for(const doc of ['- first\n- second','1. first\n2. second']){
        const view=editor(doc);press(view,'Tab');assert.match(view.state.doc.line(2).text,/^ {4}/);assert.match(syntaxTree(view.state).toString(),/ListItem\(ListMark,Paragraph,.*List/);
        press(view,'Enter');assert.match(view.state.doc.line(3).text,/^ {4}(?:-|3\.) /);press(view,'Tab',true);assert.doesNotMatch(view.state.doc.line(3).text,/^ /);
    }
});
test('Tab and Shift-Tab also indent ordinary text',()=>{const view=editor('text');press(view,'Tab');assert.equal(view.state.doc.toString(),'    text');press(view,'Tab',true);assert.equal(view.state.doc.toString(),'text');});
