import test from 'node:test';
import assert from 'node:assert/strict';
import * as Y from 'yjs';
import {Awareness} from 'y-protocols/awareness';
import {EditorState,EditorSelection} from '@codemirror/state';
import {textPresence,textPeers} from './presence.js';

function fixture(){
    const doc=new Y.Doc(),text=doc.getText('markdown');text.insert(0,'environment');
    const states=new Map([[doc.clientID,{user:{name:'Local'}}],[doc.clientID+1,{user:{name:'Remote',color:'#123456'},cursor:{
        anchor:Y.createRelativePositionFromTypeIndex(text,3),head:Y.createRelativePositionFromTypeIndex(text,7)
    }}]]);
    const listeners=new Map(),awareness={getStates:()=>states,getLocalState:()=>states.get(doc.clientID),setLocalStateField:(key,value)=>{states.get(doc.clientID)[key]=value;},on:(type,listener)=>listeners.set(type,listener),off:type=>listeners.delete(type)};
    const view={state:EditorState.create({doc:text.toString(),selection:EditorSelection.range(2,6)}),hasFocus:true,dom:{ownerDocument:{hasFocus:()=>true}},dispatch(){}};
    return {doc,states,awareness,view,listeners,provider:{doc,awareness}};
}
test('a remote selection inside a valid word does not insert inline spelling boundaries',()=>{
    const f=fixture(),plugin=textPresence(f.provider).create(f.view);
    try{assert.equal(plugin.decorations?.size??0,0,'presence must draw outside the editable text DOM');assert.equal(f.view.state.doc.toString(),'environment');}
    finally{plugin.destroy();f.doc.destroy();}
});

test('remote overlay positions follow Yjs edits and ignore other text fields',()=>{
    const f=fixture();
    try{
        assert.deepEqual(textPeers(f.provider,11).map(p=>[p.anchor,p.head]),[[3,7]]);
        f.doc.getText('markdown').insert(0,'new ');
        assert.deepEqual(textPeers(f.provider,15).map(p=>[p.anchor,p.head]),[[7,11]]);
        const other=f.doc.getText('other');other.insert(0,'text');
        f.states.set(f.doc.clientID+2,{user:{name:'Other'},cursor:{anchor:Y.createRelativePositionFromTypeIndex(other,0),head:Y.createRelativePositionFromTypeIndex(other,2)}});
        assert.equal(textPeers(f.provider,15).length,1);
        f.states.delete(f.doc.clientID+1);assert.deepEqual(textPeers(f.provider,15),[]);
    }finally{f.doc.destroy();}
});
test('local selections are published without inline widgets and cleared on blur and disposal',()=>{
    const f=fixture(),plugin=textPresence(f.provider).create(f.view);
    const selection=()=>{const cursor=f.awareness.getLocalState().cursor;return cursor?[cursor.anchor,cursor.head].map(pos=>Y.createAbsolutePositionFromRelativePosition(pos,f.doc).index):null;};
    try{
        assert.deepEqual(selection(),[2,6]);
        f.view.state=f.view.state.update({selection:{anchor:5}}).state;plugin.update({selectionSet:true});assert.deepEqual(selection(),[5,5]);
        f.view.hasFocus=false;plugin.update({focusChanged:true});assert.equal(selection(),null);
        f.view.hasFocus=true;plugin.update({focusChanged:true});assert.deepEqual(selection(),[5,5]);
        plugin.destroy();assert.equal(selection(),null);assert.equal(f.listeners.size,0);
    }finally{f.doc.destroy();}
});
test('a focused editor republishes its cursor when sync restores local awareness',()=>{
    const f=fixture();f.states.delete(f.doc.clientID);
    const plugin=textPresence(f.provider).create(f.view);
    try{
        f.states.set(f.doc.clientID,{user:{name:'Local'}});
        f.listeners.get('change')({added:[f.doc.clientID],updated:[],removed:[]});
        assert.ok(f.awareness.getLocalState().cursor,'restoring write access must restore the focused caret without another edit');
    }finally{plugin.destroy();f.doc.destroy();}
});
test('Safari context-menu focus is determined by CodeMirror rather than document.hasFocus',()=>{
    const f=fixture();f.view.dom.ownerDocument.hasFocus=()=>false;
    const plugin=textPresence(f.provider).create(f.view);
    try{assert.ok(f.awareness.getLocalState().cursor,'CodeMirror preserves editor focus while Safari dismisses native menus');}
    finally{plugin.destroy();f.doc.destroy();}
});

test('restoring real awareness publishes once without a recursive update loop',()=>{
    const f=fixture(),awareness=new Awareness(f.doc);awareness.setLocalState(null);f.provider.awareness=awareness;
    const plugin=textPresence(f.provider).create(f.view);let updates=0;awareness.on('update',()=>updates++);
    try{
        awareness.setLocalState({user:{name:'Local'}});
        assert.ok(awareness.getLocalState().cursor);assert.equal(updates,2);
    }finally{plugin.destroy();awareness.destroy();f.doc.destroy();}
});
