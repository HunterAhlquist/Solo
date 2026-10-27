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

test('slow acknowledgments leave one packet in flight and merge later edits',async()=>{
    const sent=[],p=new Provider({send:async(kind,raw)=>{sent.push([kind,JSON.parse(raw)]);return 0;}},{canWrite:true});
    const remote=new Y.Doc();
    try{
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
        p.doc.getText('markdown').insert(0,'First');p.flush();await p.queue;
        for(let i=0;i<12;i++){p.doc.getText('markdown').insert(p.doc.getText('markdown').length,' next');p.flush();}
        assert.equal(p.pending.size,1,'unsent edits must not become a growing packet queue');
        const first=sent.find(([kind])=>kind==='update')[1];Y.applyUpdate(remote,decode(first.data));
        await p.receive('ack',JSON.stringify({id:first.id,seq:'1'}));await p.queue;
        const packets=sent.filter(([kind])=>kind==='update');assert.equal(packets.length,2);
        Y.applyUpdate(remote,decode(packets[1][1].data));assert.equal(remote.getText('markdown').toString(),p.doc.getText('markdown').toString());
    }finally{p.dispose();remote.destroy();}
});
test('leaving waits for the last server acknowledgment, including edits made behind an in-flight packet',async()=>{
    const sent=[],p=new Provider({send:async(kind,raw)=>{sent.push([kind,JSON.parse(raw)]);return 0;}},{canWrite:true});
    try{
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
        p.doc.getText('markdown').insert(0,'First');let finished=false;
        const saved=p.waitForSave(1500).then(value=>{finished=true;return value;});await tick();
        p.doc.getText('markdown').insert(5,' and second');
        await p.receive('ack',JSON.stringify({id:sent.find(([kind])=>kind==='update')[1].id,seq:'1'}));await tick();
        assert.equal(finished,false,'a first ack cannot discard the later unsent edit');
        const last=sent.filter(([kind])=>kind==='update').at(-1)[1];
        await p.receive('ack',JSON.stringify({id:last.id,seq:'2'}));assert.equal(await saved,true);
    }finally{p.dispose();}
});
test('a save timeout retains unacknowledged edits and a later retry can finish',async()=>{
    const p=new Provider({send:async()=>0},{canWrite:true});
    try{
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
        p.doc.getArray('ops').push([{id:'retained'}]);
        assert.equal(await p.waitForSave(30),false);assert.equal(p.pending.size,1);assert.equal(p.doc.getArray('ops').get(0).id,'retained');
        const saved=p.waitForSave(1000);await p.receive('ack',JSON.stringify({id:[...p.pending.keys()][0],seq:'1'}));assert.equal(await saved,true);
    }finally{p.dispose();}
});

test('reconnect resumes only from a completed replay, never from a later acknowledgment',async()=>{
    const sent=[],p=new Provider({send:async(kind,raw)=>{sent.push([kind,JSON.parse(raw)]);return 0;}},{canWrite:true});
    try{
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'5',updates:5,canWrite:true,syncId:p.syncId}));
        p.doc.getText('markdown').insert(0,'local');p.flush();
        await p.receive('ack',JSON.stringify({id:[...p.pending.keys()][0],seq:'8'}));
        p.connected(false);p.connected(true);await p.queue;
        assert.equal(sent.filter(([kind])=>kind==='sync').at(-1)[1].afterSeq,'5','sequences 6 and 7 may belong to a collaborator');
        const current=p.syncId;p.connected(true);
        await p.receive('synced',JSON.stringify({seq:'99',updates:99,canWrite:true,syncId:current}));
        assert.equal(p.synced,false);assert.equal(p.checkpoint,'5');
    }finally{p.dispose();}
});

function packUpdates(updates){
    const bytes=new Uint8Array(4+updates.reduce((sum,u)=>sum+4+u.length,0)),view=new DataView(bytes.buffer);
    view.setUint32(0,updates.length,true);let offset=4;
    for(const update of updates){view.setUint32(offset,update.length,true);offset+=4;bytes.set(update,offset);offset+=update.length;}
    return bytes;
}
test('batched multipart replay merges many updates and preserves atomic sketch loading',async()=>{
    const source=new Y.Doc(),updates=[];source.on('update',u=>updates.push(u));
    for(let i=0;i<20;i++)source.getArray('ops').push([{id:i,points:'x'.repeat(2000)}]);
    const p=new Provider({send:async()=>0},{canWrite:false,atomicSync:true});
    try{
        p.connected(true);const chunks=parts(encode(packUpdates(updates)));
        for(let i=chunks.length-1;i>=0;i--)await p.receive('state-batch',JSON.stringify({seq:'20',syncId:p.syncId,index:i,count:chunks.length,data:chunks[i]}));
        assert.equal(p.doc.getArray('ops').length,0);
        await p.receive('synced',JSON.stringify({seq:'20',updates:20,canWrite:false,syncId:p.syncId}));
        assert.deepEqual(p.doc.getArray('ops').toArray(),source.getArray('ops').toArray());
    }finally{p.dispose();source.destroy();}
});
test('presence coalesces behind a slow send instead of extending the durable queue',async()=>{
    let release;const sent=[],blocked=new Promise(resolve=>release=resolve);
    const p=new Provider({send:async(kind,raw)=>{sent.push([kind,JSON.parse(raw)]);if(kind==='update')await blocked;return 0;}},{canWrite:true});
    try{
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));await p.queue;
        p.doc.getText('markdown').insert(0,'pending');p.flush();await new Promise(r=>setTimeout(r,0));
        for(let i=0;i<100;i++)p.send('awareness',{data:String(i)});
        release();await p.queue;
        assert.deepEqual(sent.filter(([kind])=>kind==='awareness').map(([,p])=>p.data),['99']);
    }finally{release();p.dispose();}
});

test('acknowledgment retries reuse the packet ID and start only after a slow send finishes',async t=>{
    const sent=[];let release;const blocked=new Promise(resolve=>release=resolve);
    const p=new Provider({send:async(kind,raw)=>{sent.push([kind,JSON.parse(raw)]);if(kind==='update')await blocked;return 0;}},{canWrite:true});
    try{
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));await p.queue;
        t.mock.timers.enable({apis:['setTimeout']});
        p.doc.getText('markdown').insert(0,'retry me');p.flush();await new Promise(setImmediate);
        t.mock.timers.tick(6000);await new Promise(setImmediate);
        assert.equal(sent.filter(([kind])=>kind==='update').length,1,'a blocked multipart send must not accumulate retries');
        release();await new Promise(setImmediate);t.mock.timers.tick(25);await p.queue;
        t.mock.timers.tick(5000);await new Promise(setImmediate);t.mock.timers.tick(25);await p.queue;
        const packets=sent.filter(([kind])=>kind==='update');assert.equal(packets.length,2);assert.equal(packets[0][1].id,packets[1][1].id);
        await p.receive('ack',JSON.stringify({id:packets[1][1].id,seq:'1'}));assert.equal(await p.waitForSave(),true);
    }finally{release();p.dispose();t.mock.timers.reset();}
});
test('downloaded editors resume cursor broadcasts after offline edits and reconnect',async()=>{
    const sent=[],p=new Provider({send:async(kind,raw)=>{if(kind==='awareness')sent.push(JSON.parse(raw));return 0;}},{canWrite:true,user:{userId:'writer',name:'Writer'}});
    p.offline={canWrite:true,close(){},hasDraft:()=>false};
    try{
        await p.connected(false);p.awareness.setLocalStateField('cursor',{anchor:{},head:{}});await tick();await p.queue;
        assert.equal(sent.length,0);
        await p.connected(true);await tick();await p.queue;
        assert.ok(sent.length>0,'a dropped offline presence packet must not latch the queue forever');
    }finally{p.dispose();}
});
test('presence queued just before disconnection does not block the next online update',async()=>{
    const sent=[],p=new Provider({send:async(kind,raw)=>{if(kind==='awareness')sent.push(JSON.parse(raw));return 0;}},{canWrite:true});
    p.offline={canWrite:true,close(){},hasDraft:()=>false};
    try{
        await p.connected(true);await tick();sent.length=0;
        let release;p.queue=new Promise(resolve=>release=resolve);const pending=p.send('awareness',{data:'old'});
        p.online=false;release();await pending;
        p.online=true;await p.send('awareness',{data:'new'});
        assert.deepEqual(sent.map(packet=>packet.data),['new']);
    }finally{p.dispose();}
});
test('a close notice goes out once, only after this editor changed something, and never interrupts', async () => {
    const sent=[];let result=0;const p=new Provider({send:async(kind)=>{sent.push(kind);return kind==='closed'?result:0;}},{canWrite:true});
    try {
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
        await p.closed();assert.ok(!sent.includes('closed'),'opening and closing unchanged is not an edit');
        p.doc.getArray('ops').push([{id:'a'}]);await tick();await p.receive('ack',JSON.stringify({id:[...p.pending.keys()][0],seq:'1'}));
        result=5;await p.closed();assert.equal(sent.filter(k=>k==='closed').length,1);assert.equal(p.canWrite,true,'a lost notice is not a sync failure');
        await p.closed();assert.equal(sent.filter(k=>k==='closed').length,1);
    } finally {p.dispose();}
});
test('only presence the relay accepted is reported as delivered', async () => {
    const delivered=[];let result=0;const p=new Provider({send:async()=>result},{canWrite:true,awarenessSent:state=>delivered.push(state.stroke)});
    try {
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
        p.awareness.setLocalStateField('stroke',{id:'s',base:0,points:[[1,2,3]]});await tick();
        result=4;p.awareness.setLocalStateField('stroke',{id:'s',base:1,points:[[4,5,6]]});await tick();
        assert.deepEqual(delivered.map(s=>s.base),[0]);
    } finally {p.dispose();}
});

test('a deleted document stays read-only through reconnects and offline access changes',async()=>{
    const sent=[];const p=new Provider({send:async(kind,raw)=>{sent.push(kind);return 0;}},{canWrite:true});
    try{
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));assert.equal(p.canWrite,true);
        p.doc.getText('markdown').insert(0,'unsaved');p.markDeleted();assert.equal(p.canWrite,false);
        // A downloaded vault keeps its own binding; its access callback must not re-enable writes.
        p.offline={canWrite:true,hasDraft:()=>true,save:async()=>false};p.setWritable(true);assert.equal(p.canWrite,false);
        const syncs=sent.filter(k=>k==='sync').length;
        await p.connected(true);await tick();assert.equal(p.canWrite,false);assert.equal(sent.filter(k=>k==='sync').length,syncs,'no resync of a deleted document');
    }finally{p.offline=null;p.dispose();}
});
test('discarding a deleted document lets it close without its unsaved edits',async()=>{
    const p=new Provider({send:async()=>0},{canWrite:true});
    try{
        p.connected(true);await p.receive('synced',JSON.stringify({seq:'0',updates:0,canWrite:true}));
        p.doc.getText('markdown').insert(0,'unsaved');await tick();assert.equal(p.hasPending,true);
        p.markDeleted();const waiting=p.waitForSave(60000);
        p.discard();assert.equal(await waiting,true);assert.equal(p.hasPending,false);assert.equal(await p.waitForSave(10),true);
    }finally{p.dispose();}
});
