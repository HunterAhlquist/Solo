import test from 'node:test';
import assert from 'node:assert/strict';
import * as Y from 'yjs';
import {mountSketch} from './index.js';
import {decode} from '../sync/protocol.js';
import {installDOM} from './input-dom-fixture.js';

const settle=()=>new Promise(resolve=>setTimeout(resolve,170));
const pen=(id,x=200,y=200)=>({pointerType:'pen',pointerId:id,button:0,clientX:x,clientY:y,pressure:.7});
const touch=(id,x,y,timeStamp)=>({pointerType:'touch',pointerId:id,button:0,clientX:x,clientY:y,pressure:.5,timeStamp});
// Acknowledges every update, so the undo after the first stroke reaches the remote too.
async function fixture(t){
    const dom=installDOM(),remote=new Y.Doc();let seq=0,sketch;
    const bridge={invokeMethodAsync:async(method,kind,raw)=>{
        if(method!=='Send'||kind!=='update')return 0;
        const packet=JSON.parse(raw);Y.applyUpdate(remote,decode(packet.data));
        await sketch.receive('ack',JSON.stringify({id:packet.id,seq:String(++seq)}));return 0;
    }};
    sketch=await mountSketch(dom.root,bridge,{canWrite:true,user:{name:'Writer'},meta:{pageSize:'A5',pattern:'dots'},explosionUrl:'/unused'});
    t.after(()=>{try{sketch.dispose();remote.destroy();}finally{dom.restore();}});sketch.connected(true);await sketch.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
    const viewport=dom.root.querySelector('.design-docs-sketch-viewport'),canvas=dom.root.querySelector('canvas');
    return {sketch,viewport,canvas,ops:()=>remote.getArray('ops').length,
        drawStroke:async id=>{canvas.emit('pointerdown',pen(id));viewport.emit('pointermove',pen(id,210,220));viewport.emit('pointerup',pen(id,230,240));await settle();}};
}
test('a two-finger tap undoes and a three-finger tap redoes without drawing or moving the view',async t=>{
    const f=await fixture(t);await f.drawStroke(1);assert.equal(f.ops(),1);
    await f.sketch.command('touchDrawing',true);const view=f.canvas.style.transform;
    f.canvas.emit('pointerdown',touch(2,300,300,1000));f.viewport.emit('pointermove',touch(2,304,306,1020));
    f.canvas.emit('pointerdown',touch(3,500,300,1030));f.viewport.emit('pointermove',touch(3,493,296,1060));
    f.viewport.emit('pointerup',touch(2,304,306,1100));f.viewport.emit('pointerup',touch(3,493,296,1110));
    await settle();assert.equal(f.ops(),0,'the first finger drew nothing and the tap undid the stroke');assert.equal(f.canvas.style.transform,view,'the jitter of the tap is not a pinch');
    for(const [id,x] of [[4,300],[5,400],[6,500]])f.canvas.emit('pointerdown',touch(id,x,300,2000+id));
    for(const [id,x] of [[4,300],[5,400],[6,500]])f.viewport.emit('pointerup',touch(id,x,300,2100+id));
    await settle();assert.equal(f.ops(),1,'the tap redid the stroke');
});
test('a quick pinch still zooms and undoes nothing',async t=>{
    const f=await fixture(t);await f.drawStroke(1);const view=f.canvas.style.transform;
    f.canvas.emit('pointerdown',touch(2,300,300,1000));f.canvas.emit('pointerdown',touch(3,500,300,1010));
    f.viewport.emit('pointermove',touch(3,600,300,1040));f.viewport.emit('pointerup',touch(2,300,300,1060));f.viewport.emit('pointerup',touch(3,600,300,1070));
    await settle();assert.equal(f.ops(),1);assert.notEqual(f.canvas.style.transform,view);
});
