import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import * as Y from 'yjs';
import {Awareness,applyAwarenessUpdate} from 'y-protocols/awareness';
import {mountSketch} from './index.js';
import {decode} from '../sync/protocol.js';
import {installDOM} from './input-dom-fixture.js';
import {StrokeFeed} from './stroke-presence.js';

const contract=readFileSync(new URL('../../../../../src/Solo.Shared/Hubs/BoardHubContract.cs',import.meta.url),'utf8');
const hubLimit=Number(contract.match(/ClientMessageMaxLength\s*=\s*([\d_]+)/)[1].replaceAll('_',''));
const wait=ms=>new Promise(resolve=>setTimeout(resolve,ms));

test('a continuous coalesced Pencil stroke stays writable and saves every sample on release',async t=>{
    const dom=installDOM(),remote=new Y.Doc(),awareness=new Awareness(remote),packets=[],writable=[],chunks=new Map();let sketch;
    awareness.setLocalState(null);
    const feed=new StrokeFeed();awareness.on('change',({added,updated,removed})=>{for(const id of [...added,...updated])feed.apply(id,awareness.getStates().get(id)?.stroke);for(const id of removed)feed.delete(id);});
    const bridge={invokeMethodAsync:async(method,kind,raw)=>{
        if(method==='Writable'){writable.push(kind);return;}
        if(method!=='Send')return 0;
        packets.push({kind,size:raw.length});
        if(raw.length>hubLimit)return 3;
        const packet=JSON.parse(raw);
        if(kind==='awareness'&&packet.data.length<=32000)applyAwarenessUpdate(awareness,decode(packet.data),'relay');
        if(kind==='update'||kind==='update-part'){
            let data=packet.data;
            if(kind==='update-part'){
                const values=chunks.get(packet.id)??new Array(packet.count);chunks.set(packet.id,values);values[packet.index]=data;
                if(Array.from(values).some(value=>value===undefined))return 0;
                data=values.join('');chunks.delete(packet.id);
            }
            Y.applyUpdate(remote,decode(data));
            await sketch.receive('ack',JSON.stringify({id:packet.id,seq:'1'}));
        }
        return 0;
    }};
    sketch=await mountSketch(dom.root,bridge,{canWrite:true,user:{userId:'writer',name:'Writer',color:'#8043ad'},meta:{pageSize:'A5',pattern:'dots'},explosionUrl:'/unused'});
    t.after(()=>{try{sketch.dispose();awareness.destroy();remote.destroy();}finally{dom.restore();}});
    sketch.connected(true);await sketch.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));await wait(90);writable.length=0;
    const viewport=dom.root.querySelector('.design-docs-sketch-viewport'),canvas=dom.root.querySelector('canvas');
    const point=n=>({pointerType:'pen',pointerId:7,button:0,clientX:200+Math.sin(n/20)*70.123456789,clientY:200+Math.cos(n/30)*70.987654321,pressure:.6123456789+n%7/100});
    canvas.emit('pointerdown',point(0));
    for(let frame=0;frame<250;frame++){
        const samples=Array.from({length:4},(_,i)=>point(frame*4+i+1));
        viewport.emit('pointermove',{...samples.at(-1),getCoalescedEvents:()=>samples});
        await wait(8);
    }
    assert.ok(!writable.includes(false),'continuous drawing must not interrupt sync and erase the held stroke; packets='+JSON.stringify(packets.slice(-4)));
    const message=[...awareness.getStates().values()].find(state=>state.stroke)?.stroke,[preview]=[...feed.values()];
    assert.ok(message.points.length<1001,'each presence message carries only what collaborators lack');
    assert.ok(preview?.points.length>900,'collaborators see every sample of the held stroke, not a thinned copy; got '+preview?.points.length);
    viewport.emit('pointerup',point(1001));await wait(400);
    assert.equal(remote.getArray('ops').length,1,'release must persist one complete stroke');
    assert.equal(remote.getArray('ops').get(0).points.length,1002,'presence limits must not discard actual Pencil samples');
    assert.ok(packets.every(packet=>packet.size<=hubLimit),'all live messages must fit the hub envelope');
    assert.ok(!writable.includes(false),'saving the full stroke must stay writable');
});

test('closing commits a held Pencil stroke and waits for its acknowledgment; timeout keeps it editable',async t=>{
    const dom=installDOM(),remote=new Y.Doc(),pending=[];let sketch;
    const bridge={invokeMethodAsync:async(method,kind,raw)=>{
        if(method==='Send'&&kind==='update'){const packet=JSON.parse(raw);pending.push(packet);Y.applyUpdate(remote,decode(packet.data));}
        return 0;
    }};
    sketch=await mountSketch(dom.root,bridge,{canWrite:true,meta:{pageSize:'A5',pattern:'dots'},explosionUrl:'/unused'});
    t.after(()=>{try{sketch.dispose();remote.destroy();}finally{dom.restore();}});
    sketch.connected(true);await sketch.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));await wait(90);
    await sketch.receive('ack',JSON.stringify({id:pending.shift().id,seq:'1'}));
    const canvas=dom.root.querySelector('canvas'),viewport=dom.root.querySelector('.design-docs-sketch-viewport');
    const point={pointerType:'pen',pointerId:7,button:0,clientX:200,clientY:200,pressure:.6};
    canvas.emit('pointerdown',point);viewport.emit('pointermove',{...point,clientX:240});
    assert.equal(await sketch.waitForSave(40),false);
    assert.equal(remote.getArray('ops').length,1,'closing commits the unfinished stroke before draining');
    let finished=false;const saved=sketch.waitForSave(1000).then(value=>{finished=true;return value;});await wait(40);
    assert.equal(finished,false);await sketch.receive('ack',JSON.stringify({id:pending.shift().id,seq:'2'}));assert.equal(await saved,true);
});

test('a disconnect retains the active stroke and resends it when writing resumes',async t=>{
    const dom=installDOM(),remote=new Y.Doc();let sketch;
    const bridge={invokeMethodAsync:async(method,kind,raw)=>{
        if(method==='Send'&&kind==='update'){const packet=JSON.parse(raw);Y.applyUpdate(remote,decode(packet.data));await sketch.receive('ack',JSON.stringify({id:packet.id,seq:'2'}));}
        return 0;
    }};
    sketch=await mountSketch(dom.root,bridge,{canWrite:true,meta:{pageSize:'A5',pattern:'dots'},explosionUrl:'/unused'});
    t.after(()=>{try{sketch.dispose();remote.destroy();}finally{dom.restore();}});
    sketch.connected(true);await sketch.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));await wait(90);
    const canvas=dom.root.querySelector('canvas'),viewport=dom.root.querySelector('.design-docs-sketch-viewport');
    const point={pointerType:'pen',pointerId:7,button:0,clientX:200,clientY:200,pressure:.6};
    canvas.emit('pointerdown',point);viewport.emit('pointermove',{...point,clientX:240});sketch.connected(false);
    assert.equal(await sketch.waitForSave(30),false);
    sketch.connected(true);await sketch.receive('synced',JSON.stringify({seq:'1',updates:1,canWrite:true}));
    assert.equal(await sketch.waitForSave(1000),true);assert.equal(remote.getArray('ops').length,1);
});

test('Escape from the canvas or text tool requests a guarded close and removes its handler on dispose',async()=>{
    const dom=installDOM(),calls=[];let sketch;
    try{
        sketch=await mountSketch(dom.root,{invokeMethodAsync:async method=>{calls.push(method);return 0;}},{canWrite:true,meta:{pageSize:'A5',pattern:'dots'},explosionUrl:'/unused'});
        const canvas=dom.root.querySelector('canvas');const text=dom.document.createElement('textarea');dom.root.append(text);
        for(const target of [canvas,text]){const event=target.emit('keydown',{key:'Escape'});assert.equal(event.defaultPrevented,true);}
        assert.equal(calls.filter(method=>method==='Close').length,2);
        sketch.dispose();sketch=null;canvas.emit('keydown',{key:'Escape'});assert.equal(calls.filter(method=>method==='Close').length,2);
    }finally{sketch?.dispose();dom.restore();}
});

test('someone joining a long held stroke receives all of it while the pen rests',async t=>{
    const dom=installDOM(),remote=new Y.Doc(),awareness=new Awareness(remote);let sketch;
    awareness.setLocalState(null);
    const bridge={invokeMethodAsync:async(method,kind,raw)=>{
        if(method!=='Send')return 0;
        const packet=JSON.parse(raw);if(raw.length>hubLimit)return 3;
        if(kind==='awareness')applyAwarenessUpdate(awareness,decode(packet.data),'relay');
        if(kind==='update'){Y.applyUpdate(remote,decode(packet.data));await sketch.receive('ack',JSON.stringify({id:packet.id,seq:'1'}));}
        return 0;
    }};
    sketch=await mountSketch(dom.root,bridge,{canWrite:true,user:{userId:'writer',name:'Writer',color:'#8043ad'},meta:{pageSize:'A5',pattern:'dots'},explosionUrl:'/unused'});
    t.after(()=>{try{sketch.dispose();awareness.destroy();remote.destroy();}finally{dom.restore();}});
    sketch.connected(true);await sketch.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));await wait(90);sketch.presence([{userId:'writer'}]);
    const viewport=dom.root.querySelector('.design-docs-sketch-viewport'),canvas=dom.root.querySelector('canvas');
    const point=n=>({pointerType:'pen',pointerId:7,button:0,clientX:200+Math.sin(n/20)*70,clientY:200+Math.cos(n/30)*70,pressure:.6});
    canvas.emit('pointerdown',point(0));
    for(let frame=0;frame<165;frame++){const samples=Array.from({length:8},(_,i)=>point(frame*8+i+1));viewport.emit('pointermove',{...samples.at(-1),getCoalescedEvents:()=>samples});await wait(2);}
    await wait(200);
    // A newcomer arrives; the pen stays down and still from here on.
    const newcomer=new StrokeFeed();awareness.on('change',({added,updated,removed})=>{for(const id of [...added,...updated])newcomer.apply(id,awareness.getStates().get(id)?.stroke);for(const id of removed)newcomer.delete(id);});
    sketch.presence([{userId:'writer'},{userId:'newcomer'}]);await wait(800);
    const [preview]=[...newcomer.values()],received=preview?preview.points.filter(Boolean).length:0;
    assert.equal(received,1321,'the newcomer must get the whole held stroke without further input; got '+received);
});
