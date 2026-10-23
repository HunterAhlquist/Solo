import test from 'node:test';import assert from 'node:assert/strict';import {SketchInput} from './input.js';
const touch=id=>({pointerType:'touch',pointerId:id}),pen={pointerType:'pen',pointerId:10};
test('one finger pans by default and touch drawing is opt-in',()=>{const input=new SketchInput();assert.equal(input.start(touch(1)),'pan');input.touchDrawing=true;assert.equal(input.start(touch(2)),'draw');});
test('a palm lifting during a stroke cannot release Pencil priority',()=>{
    const input=new SketchInput();input.start(pen);assert.equal(input.start(touch(1)),'ignore');assert.equal(input.finish(touch(1)),true);
    assert.equal(input.start(touch(2)),'ignore');input.finish(pen);assert.equal(input.ignores(touch(2)),true);input.finish(touch(2));assert.equal(input.start(touch(3)),'pan');
});
