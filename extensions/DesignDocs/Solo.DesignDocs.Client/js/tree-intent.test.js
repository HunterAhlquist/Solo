import test from 'node:test';import assert from 'node:assert/strict';import {TreeDragIntent} from './tree-intent.js';
const event=(x=0,type='touch')=>({pointerId:1,pointerType:type,clientX:x,clientY:0});
function fixture(){let tick,delay,lifts=0;const intent=new TreeDragIntent(()=>lifts++,(callback,ms)=>{tick=callback;delay=ms;return 1;},()=>{tick=null;});return {intent,hold:()=>tick?.(),delay:()=>delay,lifts:()=>lifts};}
test('touch taps remain taps; a 320 ms stationary hold lifts the title',()=>{
    const f=fixture();f.intent.begin(event(),{});assert.equal(f.delay(),320);assert.equal(f.lifts(),0);f.intent.finish();f.hold();assert.equal(f.lifts(),0);
    f.intent.begin(event(),{});f.intent.move(event(8));f.hold();assert.equal(f.lifts(),1);assert.equal(f.intent.press.active,true);
});
test('touch movement before the hold stays scrolling and cancels the pending lift',()=>{const f=fixture();f.intent.begin(event(),{});f.intent.move(event(11));f.hold();assert.equal(f.intent.press,null);assert.equal(f.lifts(),0);});
test('a fine pointer lifts after 6 px without a hold and ignores another pointer',()=>{const f=fixture();f.intent.begin(event(0,'mouse'),{});f.intent.move({...event(20,'mouse'),pointerId:2});assert.equal(f.lifts(),0);f.intent.move(event(7,'mouse'));assert.equal(f.lifts(),1);f.intent.move(event(30,'mouse'));assert.equal(f.lifts(),1);});
test('browser timer functions keep their window receiver rather than the drag object',()=>{
    const originalSet=globalThis.setTimeout,originalClear=globalThis.clearTimeout;
    try{
        globalThis.setTimeout=function(){assert.ok(!(this instanceof TreeDragIntent));return 1;};
        globalThis.clearTimeout=function(){assert.ok(!(this instanceof TreeDragIntent));};
        const intent=new TreeDragIntent(()=>{});intent.begin(event(),{});intent.finish();
    }finally{globalThis.setTimeout=originalSet;globalThis.clearTimeout=originalClear;}
});
