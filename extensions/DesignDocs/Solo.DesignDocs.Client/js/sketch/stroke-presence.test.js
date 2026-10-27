import test from 'node:test';
import assert from 'node:assert/strict';
import {strokePresence,StrokeFeed,presenceLimit} from './stroke-presence.js';

const held=(length,tool='pen')=>({id:'held',tool,ink:'theme',level:2,pointerType:'pen',points:Array.from({length},(_,i)=>[i/7,Math.sin(i/10),.6])});
const rounded=points=>points.map(([x,y,p])=>[Math.round(x*100)/100,Math.round(y*100)/100,Math.round(p*1000)/1000]);

test('messages carry only undelivered samples and a feed rebuilds every one of them',()=>{
    for(const tool of ['pen','eraser']){
        const stroke=held(0,tool),feed=new StrokeFeed();let delivered=0;
        for(let i=0;i<10000;i++){
            stroke.points.push([i/7,Math.sin(i/10),.6]);
            if(i%13)continue;
            const message=strokePresence(stroke,delivered);
            assert.ok(message.points.length<=presenceLimit);
            feed.apply(1,message);delivered=message.base+message.points.length;
        }
        const last=strokePresence(stroke,delivered);feed.apply(1,last);
        const [preview]=[...feed.values()];
        assert.equal(preview.points.length,10000,'a long held stroke must not be thinned for collaborators');
        assert.deepEqual(preview.points,rounded(stroke.points));
        assert.equal(stroke.points.length,10000,'presence never changes the saved samples');
    }
});
test('a resent overlap replaces rather than duplicates, and a gap joins across',()=>{
    const stroke=held(100),feed=new StrokeFeed();
    feed.apply(7,strokePresence(stroke,0));stroke.points.push([50,50,.5],[60,60,.5]);
    feed.apply(7,strokePresence(stroke,90));
    assert.equal([...feed.values()][0].points.length,102);
    for(let i=0;i<presenceLimit+50;i++)stroke.points.push([i,i,.5]);
    const late=strokePresence(stroke,102);assert.equal(late.points.length,presenceLimit,'the backlog is bounded');
    feed.apply(7,late);const preview=[...feed.values()][0];
    assert.equal(preview.points.length,102+presenceLimit);assert.deepEqual(preview.points.at(-1),stroke.points.at(-1));
    stroke.points.push([1,1,.5]);feed.apply(7,strokePresence(stroke,stroke.points.length-1));
    assert.equal(preview.points.length,103+presenceLimit,'later messages still line up after a gap');
});
test('a late joiner starts from what it receives; a new or finished stroke clears the old one',()=>{
    const stroke=held(300),feed=new StrokeFeed();
    feed.apply(2,strokePresence(stroke,250));assert.equal([...feed.values()][0].points.length,50);
    feed.apply(2,strokePresence(stroke,0));assert.equal([...feed.values()][0].points.length,300,'a resend from the start fills it in');
    feed.apply(2,strokePresence({...held(3),id:'next'},0));assert.equal([...feed.values()][0].points.length,3);
    feed.apply(2,null);assert.equal(feed.held.size,0);
    feed.apply(3,{id:'old',tool:'pen',points:[[1,2,.3],[4,5,.6]]});feed.apply(3,{id:'old',tool:'pen',points:[[1,2,.3],[4,5,.6],[6,7,.9]]});
    assert.equal([...feed.values()][0].points.length,3,'a whole stroke from an older client replaces');
});
test('short previews retain every sample and non-brush tools have no stroke presence',()=>{
    const stroke={tool:'pen',points:[[1,2,.3],[4,5,.6]]},preview=strokePresence(stroke);
    assert.deepEqual(preview,{...stroke,base:0});stroke.points.push([6,7,.9]);assert.equal(preview.points.length,2);
    assert.equal(strokePresence(null),null);assert.equal(strokePresence({...stroke,tool:'rectangle'}),null);
});
