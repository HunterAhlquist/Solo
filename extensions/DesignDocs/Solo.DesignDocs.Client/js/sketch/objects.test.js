import test from 'node:test';import assert from 'node:assert/strict';import * as Y from 'yjs';
import {moveStroke,sketchStrokes} from './objects.js';
import {rasterize,splitSelection} from './render.js';
import {selectPolygon} from './selection.js';
const stroke=(id,y,tool='pen')=>({id,tool,ink:'theme',level:0,pointerType:'mouse',seed:27,points:[[5,y,1],[30,y,1]]});
test('moving ink places it above older strokes and erasers, with undo restoring the original order',()=>{
    const doc=new Y.Doc(),a=stroke('a',10),b=stroke('b',30),eraser=stroke('e',30,'eraser');doc.getArray('ops').push([a,b,eraser]);
    const region=selectPolygon(rasterize([a,b,eraser],50,50),50,50,[[0,0],[40,0],[40,20],[0,20]]).region;
    const undo=new Y.UndoManager([doc.getArray('ops'),doc.getMap('cuts')]);
    doc.transact(()=>moveStroke(doc,a,splitSelection([a],region,50,50,0,20,new Set(['a']))));
    const moved=sketchStrokes(doc);assert.ok(moved.findIndex(s=>s.id===eraser.id)<moved.findIndex(s=>s.id!=='b'&&s.id!=='e'));
    assert.ok(rasterize(moved,50,50).pixels.slice(27*50,34*50).some(Boolean),'an old eraser must not erase ink moved onto it');
    undo.undo();assert.deepEqual(sketchStrokes(doc),[a,b,eraser]);undo.redo();assert.deepEqual(sketchStrokes(doc),moved);undo.destroy();doc.destroy();
});
test('concurrent moves of one object converge to one placement and keep unrelated ink',()=>{
    const base=new Y.Doc(),a=new Y.Doc(),b=new Y.Doc(),original=stroke('original',10);base.getArray('ops').push([original,stroke('untouched',40)]);
    for(const doc of [a,b])Y.applyUpdate(doc,Y.encodeStateAsUpdate(base));
    for(const [doc,y]of [[a,20],[b,30]])moveStroke(doc,original,{stationary:[],moved:[stroke(crypto.randomUUID(),y)]});
    Y.applyUpdate(a,Y.encodeStateAsUpdate(b));Y.applyUpdate(b,Y.encodeStateAsUpdate(a));
    assert.deepEqual(sketchStrokes(a),sketchStrokes(b));assert.equal(sketchStrokes(a).length,2);assert.equal(sketchStrokes(a).filter(s=>s.id==='untouched').length,1);
    base.destroy();a.destroy();b.destroy();
});
