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
test('a resent overlap changes nothing, and a gap is a hole until it arrives',()=>{
    const stroke=held(100),feed=new StrokeFeed();
    feed.apply(7,strokePresence(stroke,0));const preview=[...feed.values()][0],revision=preview.revision;
    stroke.points.push([50,50,.5],[60,60,.5]);feed.apply(7,strokePresence(stroke,90));
    assert.equal(preview.points.length,102);assert.equal(preview.revision,revision,'an identical overlap is not a change');
    for(let i=0;i<300;i++)stroke.points.push([i,i,.5]);
    feed.apply(7,strokePresence(stroke,200));assert.equal(preview.points.length,402);assert.equal(preview.points[150],undefined);
    feed.apply(7,strokePresence(stroke,102));assert.deepEqual(preview.points,rounded(stroke.points));
    assert.ok(preview.revision>revision,'filling a hole tells a renderer to start over');
});
test('someone joining a long held stroke is filled in from its start',()=>{
    const stroke=held(presenceLimit*2+300),existing=new StrokeFeed(),newcomer=new StrokeFeed();
    let delivered=0;while(delivered<stroke.points.length){const message=strokePresence(stroke,delivered);existing.apply(1,message);delivered=message.base+message.points.length;}
    const before=[...existing.values()][0].revision;
    // peerJoined resets delivery to zero; bounded messages then resend the stroke oldest first.
    delivered=0;let messages=0;
    while(delivered<stroke.points.length){
        const message=strokePresence(stroke,delivered);assert.ok(message.points.length<=presenceLimit);
        existing.apply(1,message);newcomer.apply(1,message);delivered=message.base+message.points.length;messages++;
    }
    assert.equal(messages,3);assert.deepEqual([...newcomer.values()][0].points,rounded(stroke.points),'the newcomer has the whole stroke, not its last half');
    assert.equal([...existing.values()][0].revision,before,'the resend leaves what others already had untouched');
});
test('a new or finished stroke clears the old one, and older clients send whole strokes',()=>{
    const feed=new StrokeFeed();feed.apply(2,strokePresence(held(300),0));
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
