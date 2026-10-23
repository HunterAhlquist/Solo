import test from 'node:test';
import assert from 'node:assert/strict';
import * as Y from 'yjs';
import {mountSketch} from './index.js';
import {decode} from '../sync/protocol.js';
import {installDOM} from './input-dom-fixture.js';
async function fixture(t){
    const dom=installDOM(),remote=new Y.Doc();
    const sketch=await mountSketch(dom.root,{invokeMethodAsync:async(method,kind,raw)=>{if(method==='Send'&&kind==='update')Y.applyUpdate(remote,decode(JSON.parse(raw).data));return 0;}},{canWrite:true,user:{name:'Writer'},meta:{pageSize:'A5',pattern:'dots'},explosionUrl:'/unused'});
    t.after(()=>{try{sketch.dispose();remote.destroy();}finally{dom.restore();}});sketch.connected(true);await sketch.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
    return {...dom,sketch,remote,viewport:dom.root.querySelector('.design-docs-sketch-viewport'),canvas:dom.root.querySelector('canvas')};
}
const pointer=(id,x=200,y=200)=>({pointerType:'pen',pointerId:id,button:0,clientX:x,clientY:y,pressure:.7});
test('rapid Pencil strokes suppress the native touch default that can steal input for selection',async t=>{
    const f=await fixture(t);
    for(let i=0;i<40;i++){
        f.canvas.emit('pointerdown',pointer(i));
        const native=f.canvas.emit('touchstart',{changedTouches:[{identifier:i,touchType:'stylus'}]});
        assert.equal(native.defaultPrevented,true,'Pencil touchstart must not be available to the system selection recognizer');
        f.viewport.emit('pointermove',pointer(i,210,220));
        assert.equal(f.canvas.emit('touchmove').defaultPrevented,true);
        f.viewport.emit('pointerup',pointer(i,230,240));f.canvas.emit('touchend');
    }
    await new Promise(resolve=>setTimeout(resolve,170));
    assert.equal(f.remote.getArray('ops').length,40,'every quick stroke reaches the shared document once');
});
test('native selection and context menus cannot remain outside sketch text fields',async t=>{
    const f=await fixture(t);f.selection.rangeCount=1;f.selection.anchorNode=f.canvas;f.selection.focusNode=f.canvas;
    f.document.emit('selectionchange');assert.equal(f.selection.rangeCount,0,'clear a native selection even when Safari skipped selectstart');
    assert.equal(f.viewport.emit('contextmenu').defaultPrevented,true,'blank drawing view must suppress the menu too');
    const text=f.document.createElement('textarea');f.viewport.append(text);text.focus();
    f.selection.rangeCount=1;f.selection.anchorNode=text;f.selection.focusNode=text;f.document.emit('selectionchange');
    assert.equal(f.selection.rangeCount,1);assert.equal(text.emit('touchstart').defaultPrevented,false);assert.equal(text.emit('contextmenu').defaultPrevented,false);
});
test('Pencil cancellation recovers for the next stroke and a palm cannot finish that stroke',async t=>{
    const f=await fixture(t);f.canvas.emit('pointerdown',pointer(10));f.viewport.emit('pointermove',pointer(10,210,220));f.viewport.emit('pointercancel',pointer(10));
    f.canvas.emit('pointerdown',pointer(11));f.viewport.emit('pointermove',pointer(11,220,240));
    const palm={pointerType:'touch',pointerId:50,button:0,clientX:400,clientY:400};
    f.canvas.emit('pointerdown',palm);f.viewport.emit('pointerup',palm);f.viewport.emit('pointerup',pointer(11,230,250));
    await new Promise(resolve=>setTimeout(resolve,170));
    const ops=f.remote.getArray('ops').toArray();assert.equal(ops.length,1);assert.equal(ops[0].pointerId,11);assert.equal(ops[0].points.length,3);
});
test('native-touch cancellation leaves single-finger panning and two-finger gestures to Pointer Events',async t=>{
    const f=await fixture(t),touch=(id,x,y)=>({pointerType:'touch',pointerId:id,button:0,clientX:x,clientY:y,pressure:.5});
    const before=f.canvas.style.transform;
    f.canvas.emit('pointerdown',touch(1,200,200));assert.equal(f.canvas.emit('touchstart').defaultPrevented,true);
    f.viewport.emit('pointermove',touch(1,240,250));assert.notEqual(f.canvas.style.transform,before);
    f.canvas.emit('pointerdown',touch(2,400,400));const pan=f.canvas.style.transform;
    f.viewport.emit('pointermove',touch(2,450,500));assert.notEqual(f.canvas.style.transform,pan);
    f.viewport.emit('pointerup',touch(1,240,250));f.viewport.emit('pointerup',touch(2,450,500));
    assert.equal(f.remote.getArray('ops').length,0);
});
test('native guards leave toolbar clicks usable and detach when the sketch closes',async t=>{
    const f=await fixture(t),button=f.document.createElement('button');f.dialog.append(button);
    assert.equal(button.emit('touchstart').defaultPrevented,false);
    assert.equal(button.emit('mousedown').defaultPrevented,false);
    assert.equal(button.emit('contextmenu').defaultPrevented,true);
    f.sketch.dispose();f.selection.rangeCount=1;f.document.emit('selectionchange');assert.equal(f.selection.rangeCount,1);
    assert.equal(button.emit('contextmenu').defaultPrevented,false);
});
