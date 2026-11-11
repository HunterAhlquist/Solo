import test from 'node:test';
import assert from 'node:assert/strict';
import {mountSketch} from './index.js';
import {installDOM} from './input-dom-fixture.js';

async function fixture(t){
    const dom=installDOM();
    const sketch=await mountSketch(dom.root,{invokeMethodAsync:async()=>0},{canWrite:true,user:{name:'Writer'},meta:{pageSize:'A5',pattern:'dots'},explosionUrl:'/unused'});
    t.after(()=>{try{sketch.dispose();}finally{dom.restore();}});sketch.connected(true);await sketch.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
    await sketch.command('tool','text');
    const viewport=dom.root.querySelector('.design-docs-sketch-viewport'),canvas=dom.root.querySelector('canvas');
    return {viewport,canvas,input:()=>viewport.querySelector('textarea')};
}
const at=(pointerType,pointerId)=>({pointerType,pointerId,button:0,clientX:200,clientY:200,pressure:.5});

test('a box opened with Pencil keeps the virtual keyboard away',async t=>{
    const f=await fixture(t);f.canvas.emit('pointerdown',at('pen',1));
    assert.equal(f.input().inputMode,'none');
});
test('a finger on the open box brings the keyboard and Pencil puts it away again',async t=>{
    const f=await fixture(t);f.canvas.emit('pointerdown',at('pen',1));const input=f.input();
    input.emit('pointerdown',at('touch',2));assert.equal(input.inputMode,'text');
    input.emit('pointerdown',at('pen',3));assert.equal(input.inputMode,'none');
});
test('a mouse leaves the keyboard choice to the platform',async t=>{
    const f=await fixture(t);f.canvas.emit('pointerdown',at('mouse',1));const input=f.input();
    assert.equal(input.inputMode,undefined);
    input.emit('pointerdown',at('mouse',1));assert.equal(input.inputMode,undefined);
});
