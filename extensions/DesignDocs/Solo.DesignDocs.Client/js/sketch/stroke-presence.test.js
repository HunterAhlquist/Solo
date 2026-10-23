import test from 'node:test';
import assert from 'node:assert/strict';
import {strokePresence} from './stroke-presence.js';

test('long pen and eraser previews keep both ends without changing saved samples',()=>{
    for(const tool of ['pen','eraser']){
        const points=Array.from({length:10000},(_,i)=>[i/7,Math.sin(i/10),.6]),stroke={id:'held',tool,ink:'theme',level:2,pointerType:'pen',points};
        const preview=strokePresence(stroke);
        assert.equal(preview.points.length,128);assert.equal(preview.points[0],points[0]);assert.equal(preview.points.at(-1),points.at(-1));
        assert.equal(stroke.points,points);assert.equal(stroke.points.length,10000);
        points.push([1,2,.7]);assert.equal(preview.points.length,128,'later input cannot expand an already queued preview');
    }
});
test('short previews retain every sample and non-brush tools have no stroke presence',()=>{
    const stroke={tool:'pen',points:[[1,2,.3],[4,5,.6]]},preview=strokePresence(stroke);
    assert.deepEqual(preview,stroke);stroke.points.push([6,7,.9]);assert.equal(preview.points.length,2);
    assert.equal(strokePresence(null),null);assert.equal(strokePresence({...stroke,tool:'rectangle'}),null);
});
