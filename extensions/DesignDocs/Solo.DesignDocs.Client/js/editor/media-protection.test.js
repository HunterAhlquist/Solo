import test from 'node:test';
import assert from 'node:assert/strict';
import { EditorState } from '@codemirror/state';
import { markdown } from '@codemirror/lang-markdown';
import { livePreview } from './preview.js';
import { protectMedia,mediaEdit } from './media-protection.js';
function state(){const field=livePreview({},{});return EditorState.create({doc:'Hello ![[a.png]] ![[Map.sketch]] end',extensions:[markdown(),field,protectMedia(field)]});}
test('backspace/delete cannot remove photos, sketches or a selection spanning them',()=>{
    for(const [from,to]of [[6,16],[17,32],[0,35]])for(const userEvent of ['delete.backward','delete.forward','delete.selection']){
        const s=state(),next=s.update({changes:{from,to:Math.min(to,s.doc.length)},userEvent}).state;assert.equal(next.doc.toString(),s.doc.toString());
    }
});
test('normal text deletion and explicit viewer removal still work',()=>{
    const s=state();assert.equal(s.update({changes:{from:0,to:1},userEvent:'delete.backward'}).state.doc.toString(),s.doc.toString().slice(1));
    const next=s.update({changes:{from:6,to:16},userEvent:'delete.selection',annotations:mediaEdit.of(true)}).state;
    assert.ok(!next.doc.toString().includes('a.png'));
});

test('typing, paste, drop, and IME cannot replace a selection containing attachments',()=>{
    for(const userEvent of ['input.type','input.paste','input.drop','input.type.compose']){
        const s=state();assert.equal(s.update({changes:{from:0,to:s.doc.length,insert:'Replacement'},userEvent}).state.doc.toString(),s.doc.toString(),userEvent);
        assert.equal(s.update({changes:{from:0,to:5,insert:'Hi'},userEvent}).state.doc.toString(),'Hi ![[a.png]] ![[Map.sketch]] end');
    }
});
test('explicit media moves and remote changes still apply',()=>{
    const s=state(),changes=[{from:6,to:16,insert:''},{from:s.doc.length,insert:' ![[a.png]]'}];
    assert.equal(s.update({changes,userEvent:'input.drop',annotations:mediaEdit.of(true)}).state.doc.toString(),'Hello  ![[Map.sketch]] end ![[a.png]]');
    assert.equal(s.update({changes:{from:0,to:s.doc.length,insert:'Remote'}}).state.doc.toString(),'Remote');
});
