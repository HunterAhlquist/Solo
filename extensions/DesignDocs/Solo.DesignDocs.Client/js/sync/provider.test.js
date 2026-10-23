import test from 'node:test';
import assert from 'node:assert/strict';
import * as Y from 'yjs';
import { Provider } from './provider.js';
import { encode, decode, parts, sequence } from './protocol.js';
const tick = () => new Promise(r => setTimeout(r, 90));
test('bytes, chunk boundaries and large sequence numbers round trip', () => {
    const data = Uint8Array.from({length:90000}, (_, i) => i % 256);
    assert.deepEqual(decode(parts(encode(data)).join('')), data);
    assert.equal(sequence('9007199254740993','9007199254740992'),'9007199254740993');
});
test('pending changes survive a gap and duplicate replay converges', async () => {
    const sent = []; const p = new Provider({send: async(kind,raw) => {sent.push([kind,JSON.parse(raw)]);return 0;}}, {canWrite:true});
    const remote = new Y.Doc();
    try {
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
        p.doc.getText('markdown').insert(0,'Hello');await tick();
        assert.equal(p.pending.size,1);p.connected(false);p.connected(true);
        await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));await tick();
        const packets=sent.filter(([k])=>k==='update');assert.ok(packets.length>=2);
        for(const [,packet] of packets.reverse())Y.applyUpdate(remote,decode(packet.data));
        assert.equal(remote.getText('markdown').toString(),'Hello');
        const packet=packets[0][1];await p.receive('ack',JSON.stringify({id:packet.id,seq:'1'}));assert.equal(p.pending.size,0);
    } finally {p.dispose();remote.destroy();}
});
test('multipart state applies only after every reordered part arrives', async () => {
    const source=new Y.Doc();source.getText('markdown').insert(0,'x'.repeat(80000));
    const p=new Provider({send:async()=>0},{canWrite:false});
    try {
        const chunks=parts(encode(Y.encodeStateAsUpdate(source)));
        for(let i=chunks.length-1;i>=0;i--){await p.receive('state',JSON.stringify({seq:'7',index:i,count:chunks.length,data:chunks[i]}));if(i>0)assert.equal(p.doc.getText('markdown').length,0);}
        assert.equal(p.doc.getText('markdown').length,80000);assert.equal(p.seq,'7');
    }finally{p.dispose();source.destroy();}
});
test('returning from a hidden tab explicitly resynchronizes and releases its listener',async()=>{
    const fake=new EventTarget();fake.hidden=true;globalThis.document=fake;const sent=[];
    const p=new Provider({send:async(kind,data)=>{sent.push(kind);return 0;}},{canWrite:false});
    try{p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:false}));await tick();sent.length=0;
        fake.hidden=false;fake.dispatchEvent(new Event('visibilitychange'));await tick();assert.ok(sent.includes('sync'));assert.equal(p.synced,false);
        p.dispose();sent.length=0;fake.dispatchEvent(new Event('visibilitychange'));await tick();assert.equal(sent.length,0);
    }finally{p.dispose();delete globalThis.document;}
});

test('a sketch opens at its current state without replaying visible intermediate history',async()=>{
    const source=new Y.Doc(),updates=[];source.on('update',update=>updates.push(update));
    const ops=source.getArray('ops');ops.push([{id:'old'}]);ops.delete(0,1);ops.push([{id:'current'}]);
    const provider=new Provider({send:async()=>0},{canWrite:false,atomicSync:true});
    const visible=[];provider.doc.getArray('ops').observe(()=>visible.push(provider.doc.getArray('ops').toArray()));
    try{
        provider.connected(true);
        for(const [index,update]of updates.entries())await provider.receive('update',JSON.stringify({seq:String(index+1),index:0,count:1,data:encode(update)}));
        assert.deepEqual(visible,[],'loading must not display old/deleted strokes');
        await provider.receive('synced',JSON.stringify({seq:'3',updates:3,canWrite:false}));
        assert.deepEqual(visible,[[{id:'current'}]]);
    }finally{provider.dispose();source.destroy();}
});

test('an interrupted atomic sketch load resumes from the last applied state, then live updates apply immediately',async()=>{
    const source=new Y.Doc(),updates=[];source.on('update',update=>updates.push(update));const ops=source.getArray('ops');ops.push([{id:'one'}]);ops.push([{id:'two'}]);
    const provider=new Provider({send:async()=>0},{canWrite:false,atomicSync:true});
    const packet=index=>JSON.stringify({seq:String(index+1),index:0,count:1,data:encode(updates[index])});
    try{
        provider.connected(true);await provider.receive('update',packet(0));assert.equal(provider.seq,'0');provider.connected(false);provider.connected(true);
        await provider.receive('update',packet(0));await provider.receive('update',packet(1));await provider.receive('synced',JSON.stringify({seq:'2',updates:2,canWrite:false}));
        assert.deepEqual(provider.doc.getArray('ops').toArray(),[{id:'one'},{id:'two'}]);assert.equal(provider.seq,'2');
        ops.push([{id:'live'}]);await provider.receive('update',packet(2));assert.equal(provider.doc.getArray('ops').length,3);assert.equal(provider.seq,'3');
    }finally{provider.dispose();source.destroy();}
});

test('continuous pointer or caret changes publish awareness before the user stops moving',async()=>{
    const sent=[],p=new Provider({send:async(kind,raw)=>{if(kind==='awareness')sent.push(JSON.parse(raw));return 0;}},{canWrite:true,user:{userId:'writer',name:'Writer',color:'#8043ad'}});
    try{
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
        for(let i=0;i<30;i++){p.awareness.setLocalStateField('pointer',{x:i,y:20});await new Promise(resolve=>setTimeout(resolve,8));}
        assert.ok(sent.length>=2,'remote cursors must update during continuous writing, not only after it stops');
    }finally{p.dispose();}
});
test('a newly present collaborator receives an already stationary cursor',async()=>{
    const sent=[],p=new Provider({send:async(kind,raw)=>{if(kind==='awareness')sent.push(JSON.parse(raw));return 0;}},{canWrite:true,user:{userId:'writer',name:'Writer',color:'#8043ad'}});
    try{
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));await tick();sent.length=0;
        p.presence([{userId:'writer'},{userId:'new-reader'}]);await tick();
        assert.equal(sent.length,1,'joining users must not wait for another cursor movement or the awareness heartbeat');
    }finally{p.dispose();}
});
test('writable sync creates presence after initialization and every reconnect',async()=>{
    const user={userId:'writer',name:'Writer',color:'#8043ad'},p=new Provider({send:async()=>0},{canWrite:true,user});
    try{
        for(let i=0;i<2;i++){
            p.connected(true);assert.equal(p.awareness.getLocalState(),null);
            await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
            assert.deepEqual(p.awareness.getLocalState().user,user);p.awareness.setLocalStateField('pointer',{x:10,y:20});assert.equal(p.awareness.getLocalState().pointer.x,10);
            p.connected(false);assert.equal(p.awareness.getLocalState(),null);
        }
    }finally{p.dispose();}
});

test('rejected or failed ephemeral presence never interrupts a writable document',async()=>{
    for(const failure of [3,4,5,new Error('presence send failed')]){
        const statuses=[],writable=[],p=new Provider({send:async kind=>{if(kind!=='awareness')return 0;if(failure instanceof Error)throw failure;return failure;}},{canWrite:true,status:message=>statuses.push(message),writable:value=>writable.push(value)});
        try{
            p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));await p.queue;
            statuses.length=writable.length=0;
            await p.send('awareness',{data:'ephemeral'});
            assert.equal(p.canWrite,true,'losing a cursor preview must not clear an unfinished stroke');
            assert.ok(!writable.includes(false));assert.ok(!statuses.some(message=>message.includes('interrupted')));
            p.doc.getArray('ops').push([{id:'retained stroke'}]);p.flush();await p.queue;
            assert.equal(p.pending.size,1,'durable edits still use the acknowledged update path');
        }finally{p.dispose();}
    }
});

test('a failed durable update still interrupts editing and retains the pending change',async()=>{
    const p=new Provider({send:async kind=>kind==='update'?5:0},{canWrite:true});
    try{
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));await p.queue;
        p.doc.getArray('ops').push([{id:'pending'}]);p.flush();await p.queue;
        assert.equal(p.canWrite,false);assert.equal(p.pending.size,1);assert.equal(p.doc.getArray('ops').get(0).id,'pending');
    }finally{p.dispose();}
});
