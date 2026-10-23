import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import * as Y from 'yjs';
import {Awareness,applyAwarenessUpdate} from 'y-protocols/awareness';
import {mountSketch} from './index.js';
import {decode} from '../sync/protocol.js';
import {installDOM} from './input-dom-fixture.js';

const contract=readFileSync(new URL('../../../../../src/Solo.Shared/Hubs/BoardHubContract.cs',import.meta.url),'utf8');
const hubLimit=Number(contract.match(/ClientMessageMaxLength\s*=\s*([\d_]+)/)[1].replaceAll('_',''));
const wait=ms=>new Promise(resolve=>setTimeout(resolve,ms));

test('a continuous coalesced Pencil stroke stays writable and saves every sample on release',async t=>{
    const dom=installDOM(),remote=new Y.Doc(),awareness=new Awareness(remote),packets=[],writable=[],chunks=new Map();let sketch;
    awareness.setLocalState(null);
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
    const preview=[...awareness.getStates().values()].find(state=>state.stroke)?.stroke;
    assert.ok(preview?.points.length>1,'collaborators must still see the held stroke through the relay presence limit');
    assert.ok(preview.points.length<=128,'the disposable preview stays bounded throughout a long stroke');
    viewport.emit('pointerup',point(1001));await wait(400);
    assert.equal(remote.getArray('ops').length,1,'release must persist one complete stroke');
    assert.equal(remote.getArray('ops').get(0).points.length,1002,'presence limits must not discard actual Pencil samples');
    assert.ok(packets.every(packet=>packet.size<=hubLimit),'all live messages must fit the hub envelope');
    assert.ok(!writable.includes(false),'saving the full stroke must stay writable');
});
