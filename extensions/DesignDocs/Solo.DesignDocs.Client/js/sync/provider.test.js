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
