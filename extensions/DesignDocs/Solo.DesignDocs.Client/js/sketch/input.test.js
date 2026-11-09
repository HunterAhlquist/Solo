import test from 'node:test';import assert from 'node:assert/strict';import {SketchInput,TouchTap} from './input.js';
const touch=id=>({pointerType:'touch',pointerId:id}),pen={pointerType:'pen',pointerId:10};
test('one finger pans by default and touch drawing is opt-in',()=>{const input=new SketchInput();assert.equal(input.start(touch(1)),'pan');input.touchDrawing=true;assert.equal(input.start(touch(2)),'draw');});
test('a palm lifting during a stroke cannot release Pencil priority',()=>{
    const input=new SketchInput();input.start(pen);assert.equal(input.start(touch(1)),'ignore');assert.equal(input.finish(touch(1)),true);
    assert.equal(input.start(touch(2)),'ignore');input.finish(pen);assert.equal(input.ignores(touch(2)),true);input.finish(touch(2));assert.equal(input.start(touch(3)),'pan');
});
const finger=(id,timeStamp,x=100,y=100,type='pointerup')=>({type,pointerType:'touch',pointerId:id,timeStamp,clientX:x,clientY:y});
function tapWith(recognizer,fingers,{lift=100,x=0}={}){
    for(let i=0;i<fingers;i++)recognizer.down(finger(i,i*20,100+i*80));
    let result=null;for(let i=0;i<fingers;i++)result=recognizer.up(finger(i,lift+i*20,100+i*80+x));return result;
}
test('two fingers tapped together undo and three redo',()=>{
    const tap=new TouchTap();assert.equal(tapWith(tap,2),'undo');assert.equal(tapWith(tap,3),'redo');
    assert.equal(tapWith(tap,1),null,'one finger is a pan or a dot');assert.equal(tapWith(tap,4),null);
});
test('a tap that lingers or wanders was a gesture, not a command',()=>{
    const tap=new TouchTap();
    assert.equal(tapWith(tap,2,{lift:TouchTap.maxDuration}),null);
    assert.equal(tapWith(tap,2,{x:TouchTap.slop+1}),null);
    tap.down(finger(1,0));tap.down(finger(2,10,300));tap.move(finger(2,40,300,140));assert.equal(tap.up(finger(2,60,300)),null);assert.equal(tap.up(finger(1,70)),null,'moving back does not unspoil a pinch');
    tap.down(finger(1,0));tap.down(finger(2,10,300));assert.equal(tap.up(finger(1,40,100,100,'pointercancel')),null);assert.equal(tap.up(finger(2,50,300)),null);
});
test('Pencil landing mid-tap cancels it, and fingers lifting in turn are separate taps',()=>{
    const tap=new TouchTap();tap.down(finger(1,0));tap.down(finger(2,10,300));tap.down(pen);
    assert.equal(tap.up(finger(1,40)),null);assert.equal(tap.up(finger(2,50,300)),null);
    tap.down(finger(3,0));assert.equal(tap.up(finger(3,30)),null);tap.down(finger(4,40));assert.equal(tap.up(finger(4,70)),null);
});
