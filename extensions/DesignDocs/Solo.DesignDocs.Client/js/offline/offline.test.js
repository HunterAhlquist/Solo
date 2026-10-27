import test from 'node:test';
import assert from 'node:assert/strict';
import 'fake-indexeddb/auto';
import * as Y from 'yjs';
import {VaultStore,projectVault,applyCommand,attachmentPath,order} from './store.js';
import {OfflineSession} from './session.js';
import {encode,decode} from '../sync/protocol.js';
import {sketchStrokes} from '../sketch/objects.js';
const id=()=>crypto.randomUUID();
const snapshot=(vaultId,noteId)=>({tree:{vault:{id:vaultId,name:'Park notes',isClosed:false,version:1},folders:[],notes:[{id:noteId,vaultId,folderId:null,name:'Outline',markdown:'',headings:[],version:1}]},sketches:[],images:[],documents:{[noteId]:'0'},canWrite:true,canManage:true});
const wait=ms=>new Promise(r=>setTimeout(r,ms));

test('device storage survives reopening, isolates accounts and rolls back the queue with attachment writes',async()=>{
    const scope=id(),store=new VaultStore(scope),vaultId=id(),noteId=id(),doc=new Y.Doc();await store.put('vaults',{id:vaultId,ready:true,base:snapshot(vaultId,noteId)});
    doc.getText('markdown').insert(0,'At the park');await store.updateDocument(noteId,'project',vaultId,[Y.encodeStateAsUpdate(doc)],null,true);
    const command={id:id(),vaultId,created:1,command:{entity:'note',action:'create',targetId:id(),name:'Offline note'}};await store.enqueue(command);
    await assert.rejects(store.enqueue(command,{id:'image',data:new Uint8Array([1]),vaultId}));assert.equal(await store.get('images','image'),undefined);
    await store.close();const restored=new VaultStore(scope),other=new VaultStore(scope+'-other-user');
    try{
        const record=await restored.get('documents',noteId),loaded=new Y.Doc();Y.applyUpdate(loaded,record.state);assert.equal(loaded.getText('markdown').toString(),'At the park');loaded.destroy();
        assert.equal((await restored.all('queue')).length,2);assert.equal((await other.all('queue')).length,0);
    }finally{doc.destroy();await restored.close();await other.close();}
});

test('offline document changes persist before closing and merge with concurrent remote edits after restart',async()=>{
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),projectId=id();let online=false,seq=0;
    const server=new Y.Doc(),updates=[];server.on('update',update=>updates.push(update));server.getText('markdown').insert(0,'Start');seq=1;
    const base=snapshot(vaultId,noteId);base.documents[noteId]='1';
    await store.put('vaults',{id:vaultId,projectId,ready:true,base});await store.updateDocument(noteId,projectId,vaultId,[updates[0]],'1');
    const transport=async(method,path,body)=>{
        if(!online)return {status:0};
        if(path.endsWith('/offline'))return {status:200,value:{...base,documents:{[noteId]:String(seq)}}};
        if(method==='GET'&&path.includes('/offline/documents/')){const after=Number(new URL(path,'https://test').searchParams.get('after'));return {status:200,value:{id:noteId,seq:String(seq),updates:updates.slice(after).map(encode),seedMarkdown:null}};}
        if(method==='POST'&&path.endsWith('/offline/documents/'+noteId)){const previousSeq=String(seq);Y.applyUpdate(server,decode(body.data));seq=updates.length;return {status:200,value:{seq:String(seq),previousSeq,previousVersion:null,version:null}};}
        return {status:200,value:null};
    };
    const first=new OfflineSession(transport,{instance:'test',userId:id(),store});const doc=new Y.Doc();const handle=await first.openDocument({documentId:noteId,projectId,vaultId},doc);
    doc.getText('markdown').insert(5,' · my offline text');assert.equal(await handle.save(),true);handle.close();doc.destroy();await first.running;await first.stop();
    server.getText('markdown').insert(0,'Remote · ');seq=updates.length;online=true;
    const reopened=new VaultStore(store.scope.slice(0,-1)),second=new OfflineSession(transport,{instance:'test',userId:id(),store:reopened});
    try{
        await second.sync();assert.equal((await reopened.all('queue')).length,0);
        const record=await reopened.get('documents',noteId),merged=new Y.Doc();Y.applyUpdate(merged,record.state);
        assert.equal(merged.getText('markdown').toString(),server.getText('markdown').toString());assert.ok(merged.getText('markdown').toString().includes('my offline text'));assert.ok(merged.getText('markdown').toString().includes('Remote'));merged.destroy();
    }finally{await second.stop();server.destroy();}
});

test('failed local persistence keeps the document dirty and blocks closing until a retry succeeds',async()=>{
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),projectId=id();await store.put('vaults',{id:vaultId,projectId,ready:true,base:snapshot(vaultId,noteId)});
    const client=new OfflineSession(async()=>({status:0}),{instance:'test',userId:id(),store}),doc=new Y.Doc();
    const handle=await client.openDocument({documentId:noteId,projectId,vaultId},doc);const save=store.updateDocument.bind(store);let fail=true;
    store.updateDocument=(...args)=>fail?Promise.reject(new Error('Storage full')):save(...args);
    try{doc.getText('markdown').insert(0,'Keep me');assert.equal(await handle.save(),false);assert.equal(handle.hasDraft(),true);fail=false;assert.equal(await handle.save(),true);assert.equal((await store.all('queue')).length,1);}
    finally{handle.close();doc.destroy();await client.running;await client.stop();}
});

test('metadata projection supports offline folder and note CRUD without mutating the downloaded base',()=>{
    const vaultId=id(),noteId=id(),folder=id(),base=snapshot(vaultId,noteId),vault={id:vaultId,base};
    const queue=[{id:'1',created:1,vaultId,command:{entity:'folder',action:'create',targetId:folder,name:'Chapter'}},
        {id:'2',created:2,vaultId,command:{entity:'note',action:'update',targetId:noteId,name:'Renamed',parentId:folder}}];
    const projected=projectVault(vault,queue);assert.equal(projected.tree.notes[0].name,'Renamed');assert.equal(projected.tree.notes[0].folderId,folder);assert.equal(base.tree.notes[0].name,'Outline');
    queue.push({id:'3',created:3,vaultId,command:{entity:'folder',action:'delete',targetId:folder}});assert.equal(projectVault(vault,queue).tree.notes.length,0);
});

test('concurrent cuts of one sketch object converge without duplicating it or replacing unrelated strokes',()=>{
    const base=new Y.Doc(),a=new Y.Doc(),b=new Y.Doc();
    base.getArray('ops').push([{id:'one',points:[[1,1,1]]},{id:'untouched',points:[[2,2,1]]}]);const state=Y.encodeStateAsUpdate(base);Y.applyUpdate(a,state);Y.applyUpdate(b,state);
    a.getMap('cuts').set('one',[{id:'one-cut',points:[[30,30,1]]}]);b.getMap('cuts').set('one',[{id:'one-cut',points:[[60,60,1]]}]);b.getArray('ops').push([{id:'new offline stroke',points:[[9,9,1]]}]);
    Y.applyUpdate(a,Y.encodeStateAsUpdate(b));Y.applyUpdate(b,Y.encodeStateAsUpdate(a));
    assert.deepEqual(sketchStrokes(a),sketchStrokes(b));assert.equal(sketchStrokes(a).length,3);assert.equal(sketchStrokes(a).filter(s=>s.id==='one-cut').length,1);
    base.destroy();a.destroy();b.destroy();
});

test('the editor API queues complete offline CRUD and uploads nothing through a stopped account',async()=>{
    const {startOffline,offlineRequest}=await import('./session.js');
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),projectId=id();const base=snapshot(vaultId,noteId);
    await store.put('vaults',{id:vaultId,projectId,projectAlias:'PARK',ready:true,base,color:{userId:'writer',slot:1},colors:[],list:{canManage:true,vaults:[base.tree.vault]}});
    let calls=0;const agent=await startOffline({invokeMethodAsync:async method=>{if(method==='Request')calls++;return {status:0};}},{instance:'test',userId:'writer',store});
    try{
        const root=`api/ext/design-docs/projects/${projectId}/vaults/${vaultId}`;
        const folder=(await offlineRequest('POST',root+'/folders',JSON.stringify({name:'Chapter',parentId:null}))).value;
        const note=(await offlineRequest('POST',root+'/notes',JSON.stringify({name:'Offline note',parentId:folder.id}))).value;
        await offlineRequest('PUT',root+'/notes/'+note.id,JSON.stringify({name:'Renamed',parentId:null,version:0}));
        const sketch=(await offlineRequest('POST',root+'/sketches/notes/'+note.id,JSON.stringify({name:'Drawing'}))).value;
        assert.equal(sketch.noteId,note.id);assert.ok(sketch.path.endsWith('/Drawing.sketch'));
        assert.ok(sketch.path.includes(sketch.id),'pending attachments need a unique path before merging remote embeds');
        const image=(await offlineRequest('POST',root+'/images/notes/'+note.id+'?path=photo.webp',null,new Uint8Array([7,8]),'image/webp')).value;
        assert.deepEqual((await offlineRequest('GET',root+'/images/'+image.id)).bytes,new Uint8Array([7,8]));
        let tree=(await offlineRequest('GET',root)).value;assert.equal(tree.notes.find(n=>n.id===note.id).name,'Renamed');
        await offlineRequest('DELETE',root+'/notes/'+note.id+'?version=0');tree=(await offlineRequest('GET',root)).value;
        assert.equal(tree.notes.length,1);assert.equal((await store.all('queue')).length,6);
        await agent.dispose();const before=calls;await agent.sync();assert.equal(calls,before);
    }finally{await agent.dispose();}
});

test('a downloaded vault queues permanent sketch and image deletes with the versions the server checks',async()=>{
    const {startOffline,offlineRequest}=await import('./session.js');
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),projectId=id(),sketchId=id(),imageId=id();const base=snapshot(vaultId,noteId);
    base.sketches.push({id:sketchId,noteId,name:'Map',pageSize:'A4',orientation:'portrait',pattern:'dots',path:attachmentPath(noteId,'Map.sketch'),version:4});
    base.images.push({id:imageId,path:attachmentPath(noteId,'photo.webp'),contentType:'image/webp',width:1,height:1,bytes:1,version:6});base.documents[sketchId]='3';
    await store.put('vaults',{id:vaultId,projectId,projectAlias:'PARK',ready:true,base,color:{userId:'writer',slot:1},colors:[],list:{canManage:true,vaults:[base.tree.vault]}});
    await store.put('documents',{id:sketchId,projectId,vaultId,state:Y.encodeStateAsUpdate(new Y.Doc()),seq:'3'});
    const agent=await startOffline({invokeMethodAsync:async()=>({status:0})},{instance:'test',userId:'writer',store});
    try{
        const root=`api/ext/design-docs/projects/${projectId}/vaults/${vaultId}`;
        await offlineRequest('DELETE',root+'/sketches/'+sketchId+'?version=4');
        await offlineRequest('DELETE',root+'/images/'+imageId+'?version=6');
        assert.deepEqual((await offlineRequest('GET',root+'/sketches')).value,[]);assert.deepEqual((await offlineRequest('GET',root+'/images')).value,[]);
        const queued=(await store.all('queue')).sort(order).map(row=>row.command);
        assert.deepEqual(queued.map(c=>[c.entity,c.action,c.targetId,c.version]),[['sketch','delete',sketchId,4],['image','delete',imageId,6]]);
        assert.deepEqual(queued[0].baseVersions,{[sketchId]:4});assert.deepEqual(queued[0].baseDocuments,{[sketchId]:'3'});
        await assert.rejects(offlineRequest('DELETE',root+'/sketches/'+sketchId+'?version=4'),/no longer exists/);
        await assert.rejects(offlineRequest('DELETE',root+'/images/'+imageId+'?version=6'),/no longer exists/);
    }finally{await agent.dispose();}
});

test('duplicate acknowledgments cannot rebase a later queued command to an old version',async()=>{
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),base=snapshot(vaultId,noteId);await store.put('vaults',{id:vaultId,base});
    const first={id:id(),vaultId,created:1,command:{entity:'note',action:'update',targetId:noteId,name:'First',version:1}};
    const second={id:id(),vaultId,created:2,command:{entity:'note',action:'update',targetId:noteId,name:'Second',version:1}};
    const third={id:id(),vaultId,created:3,command:{entity:'note',action:'update',targetId:noteId,name:'Third',version:1}};
    try{await store.enqueue(first);await store.enqueue(second);await store.enqueue(third);
        await store.acknowledge(first,{...base.tree.notes[0],name:'First',version:2});
        await store.acknowledge(second,{...base.tree.notes[0],name:'Second',version:3});
        await store.acknowledge(first,{...base.tree.notes[0],name:'First',version:2});
        assert.equal((await store.get('queue',third.id)).command.version,3);
        assert.equal((await store.get('vaults',vaultId)).base.tree.notes[0].name,'Second');
    }finally{await store.close();}
});

test('a sketch can draw and close after offline startup without a live connection',async()=>{
    const {startOffline}=await import('./session.js');const {installDOM}=await import('../sketch/input-dom-fixture.js');const {mountSketch}=await import('../sketch/index.js');
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),projectId=id(),sketchId=id();const base=snapshot(vaultId,noteId);base.sketches.push({id:sketchId,noteId,name:'Offline drawing',pageSize:'A5',orientation:'portrait',pattern:'dots'});
    await store.put('vaults',{id:vaultId,projectId,ready:true,base});await store.put('documents',{id:sketchId,projectId,vaultId,state:Y.encodeStateAsUpdate(new Y.Doc()),seq:'0'});
    const agent=await startOffline({invokeMethodAsync:async()=>({status:0})},{instance:'test',userId:'writer',store});const dom=installDOM();let sketch;
    try{
        sketch=await mountSketch(dom.root,{invokeMethodAsync:async()=>0},{canWrite:true,documentId:sketchId,projectId,vaultId,user:{userId:'writer'},meta:{pageSize:'A5',pattern:'dots'},explosionUrl:'/unused'});
        await sketch.connected(false);
        const canvas=dom.root.querySelector('canvas'),viewport=dom.root.querySelector('.design-docs-sketch-viewport');
        const p={pointerType:'pen',pointerId:7,button:0,clientX:200,clientY:200,pressure:.6};canvas.emit('pointerdown',p);viewport.emit('pointermove',{...p,clientX:240});
        assert.equal(await sketch.waitForSave(),true);
        const record=await store.get('documents',sketchId),persisted=new Y.Doc();Y.applyUpdate(persisted,record.state);assert.equal(persisted.getArray('ops').length,1);persisted.destroy();
    }finally{sketch?.dispose();dom.restore();await agent.dispose();}
});

test('a conflicting attachment rename preserves other users same-named embeds',async()=>{
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),projectId=id(),sketchId=id(),otherId=id();const base=snapshot(vaultId,noteId);
    const path=attachmentPath(noteId,sketchId+'/Map.sketch'),onlinePath=attachmentPath(noteId,'Map.sketch'),renamed=attachmentPath(noteId,'Map (conflict 1).sketch');
    await store.put('vaults',{id:vaultId,projectId,base});
    for(const owner of [noteId,otherId]){const doc=new Y.Doc();doc.getText('markdown').insert(0,`![[${path}|wide]] ![[Map.sketch]] ![[${onlinePath}]]\n\n\`![[${path}]]\``);await store.updateDocument(owner,projectId,vaultId,[Y.encodeStateAsUpdate(doc)]);doc.destroy();}
    const command={id:id(),projectId,vaultId,created:1,command:{entity:'sketch',action:'create',targetId:sketchId,noteId,name:'Map',path}};
    await store.enqueue(command);const result={id:sketchId,noteId,name:'Map (conflict 1)',path:renamed,version:2};
    try{
        await store.acknowledge(command,result);await store.acknowledge(command,result);
        const doc=new Y.Doc();Y.applyUpdate(doc,(await store.get('documents',noteId)).state);
        assert.equal(doc.getText('markdown').toString(),`![[${renamed}|wide]] ![[Map.sketch]] ![[${onlinePath}]]\n\n\`![[${path}]]\``);doc.destroy();
        const other=new Y.Doc();Y.applyUpdate(other,(await store.get('documents',otherId)).state);assert.ok(other.getText('markdown').toString().includes(path));other.destroy();
        assert.equal((await store.all('queue')).length,1);assert.equal((await store.all('queue'))[0].documentId,noteId);
    }finally{await store.close();}
});

test('image metadata cannot replace the projected object prototype',()=>{
    const base=snapshot(id(),id());
    applyCommand(base,{entity:'image',action:'create',targetId:'image',image:JSON.parse('{"path":"image.png","__proto__":{"polluted":true}}')});
    assert.equal(Object.getPrototypeOf(base.images[0]),Object.prototype);assert.equal(base.images[0].polluted,undefined);
});
test('offline projection rejects inherited entity names',()=>{
    for(const entity of ['__proto__','constructor','toString','hasOwnProperty'])assert.throws(()=>applyCommand(snapshot(id(),id()),{entity,action:'create',targetId:id()}),/Unknown offline entity/);
});

test('expired sign-in leaves uploads retryable after authentication resumes',async()=>{
    const store=new VaultStore(id()),vaultId=id(),projectId=id(),base=snapshot(vaultId,id());base.tree.notes=[];base.documents={};
    await store.put('vaults',{id:vaultId,projectId,base,ready:true});
    await store.enqueue({id:id(),vaultId,projectId,created:1,command:{entity:'folder',action:'create',targetId:id(),name:'Queued'}});
    let expired=true,uploads=0;
    const client=new OfflineSession(async(method)=>method==='GET'?{status:200,value:structuredClone(base)}:(uploads++,expired?{status:401,error:'Sign in again'}:{status:200,value:{id:id(),name:'Queued',version:2}}),{instance:'test',userId:id(),store});
    try{await client.sync();assert.equal((await store.all('queue'))[0].error,undefined);expired=false;await client.sync();assert.equal((await store.all('queue')).length,0);assert.equal(uploads,2);}
    finally{await client.stop();}
});

test('idle synchronization fetches one manifest and does not decode unchanged notes again',async()=>{
    const store=new VaultStore(id()),vaultId=id(),projectId=id(),noteId=id(),base=snapshot(vaultId,noteId),doc=new Y.Doc();
    doc.getText('markdown').insert(0,'Already copied');base.tree.notes[0].markdown='Already copied';base.documents[noteId]='1';
    await store.put('vaults',{id:vaultId,projectId,base,ready:true});await store.updateDocument(noteId,projectId,vaultId,[Y.encodeStateAsUpdate(doc)],'1');doc.destroy();
    let manifests=0,documentReads=0;
    const client=new OfflineSession(async()=>{manifests++;return {status:200,value:structuredClone(base)};},{instance:'test',userId:id(),store});
    const get=store.get.bind(store);store.get=(name,key)=>{if(name==='documents')documentReads++;return get(name,key);};
    try{
        await client.sync();const firstReads=documentReads;assert.equal(manifests,1);await client.sync();
        assert.equal(manifests,2);assert.equal(documentReads-firstReads,1,'an unchanged note only needs its sequence check during pull');
    }finally{await client.stop();}
});

test('requests for a named vault do not load or project unrelated downloaded vaults',async()=>{
    const {startOffline,offlineRequest}=await import('./session.js');
    const store=new VaultStore(id()),vaultId=id(),projectId=id(),base=snapshot(vaultId,id());
    await store.put('vaults',{id:vaultId,projectId,base,ready:true});
    const agent=await startOffline({invokeMethodAsync:async()=>({status:0})},{instance:'test',userId:'writer',store});
    await agent.sync();const all=store.all.bind(store);let vaultScans=0,queueScans=0;
    store.all=name=>{if(name==='vaults')vaultScans++;if(name==='queue')queueScans++;return all(name);};
    try{
        const root=`api/ext/design-docs/projects/${projectId}/vaults/`;
        assert.equal((await offlineRequest('GET',root+vaultId)).value.vault.id,vaultId);assert.equal(vaultScans,0);
        assert.equal((await offlineRequest('GET',root+id())).handled,false);assert.equal(vaultScans,0);assert.equal(queueScans,1);
        assert.equal((await offlineRequest('GET','api/projects')).handled,false);assert.equal(vaultScans,0);assert.equal(queueScans,1);
    }finally{await agent.dispose();}
});

test('closing a provider during hydration does not leave a local document attached',async()=>{
    const {startOffline}=await import('./session.js');const {Provider}=await import('../sync/provider.js');
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),projectId=id();await store.put('vaults',{id:vaultId,projectId,ready:true,base:snapshot(vaultId,noteId)});
    const agent=await startOffline({invokeMethodAsync:async()=>({status:0})},{instance:'test',userId:'writer',store});
    const original=store.get.bind(store);let release;const gate=new Promise(r=>release=r);
    store.get=async(name,key)=>{if(name==='documents')await gate;return original(name,key);};
    const provider=new Provider({send:async()=>0},{documentId:noteId,projectId,vaultId,canWrite:true,user:{userId:'writer'}});
    provider.dispose();release();await provider.offlineLoading;
    try{assert.equal(provider.offline,undefined);assert.equal((await store.all('queue')).length,0);}
    finally{await agent.dispose();}
});

test('queued updates combine into durable packages without crossing metadata commands',async()=>{
    const scope=id(),store=new VaultStore(scope),vaultId=id(),noteId=id(),projectId=id(),doc=new Y.Doc();await store.put('vaults',{id:vaultId,projectId,ready:true,base:snapshot(vaultId,noteId)});
    try{
        for(const word of ['First',' second',' third']){const vector=Y.encodeStateVector(doc);doc.getText('markdown').insert(doc.getText('markdown').length,word);await store.updateDocument(noteId,projectId,vaultId,[Y.encodeStateAsUpdate(doc,vector)],null,true);}
        const barrier={id:id(),vaultId,created:performance.timeOrigin+performance.now(),command:{entity:'note',action:'delete',targetId:noteId}};await store.enqueue(barrier);
        await store.updateDocument(noteId,projectId,vaultId,[Y.encodeStateAsUpdate(doc)],null,true);
        await store.batchUpdates(vaultId);const queue=(await store.all('queue')).sort((a,b)=>a.created-b.created);
        assert.equal(queue.length,3);assert.equal(queue[1].id,barrier.id);const merged=new Y.Doc();Y.applyUpdate(merged,queue[0].data);assert.equal(merged.getText('markdown').toString(),'First second third');merged.destroy();
        await store.close();const restored=new VaultStore(scope);assert.equal((await restored.all('queue')).length,3);await restored.close();
    }finally{doc.destroy();}
});

test('recovery includes both deleted remote notes and current notes added after an earlier deletion',async()=>{
    const {recoveryArchive}=await import('./recovery.js');const {unzipSync,strFromU8}=await import('fflate');
    const store=new VaultStore(id()),vaultId=id(),oldId=id(),currentId=id(),projectId=id();
    const base=snapshot(vaultId,currentId);base.tree.notes[0].name='Current';const recovery=snapshot(vaultId,oldId);recovery.tree.notes[0].name='Deleted';
    await store.put('vaults',{id:vaultId,projectId,base,recovery});
    for(const [noteId,text] of [[oldId,'Recovered offline text'],[currentId,'Newer note text']]){const doc=new Y.Doc();doc.getText('markdown').insert(0,text);await store.updateDocument(noteId,projectId,vaultId,[Y.encodeStateAsUpdate(doc)]);doc.destroy();}
    try{const files=unzipSync((await recoveryArchive(store,vaultId)).bytes);assert.equal(strFromU8(files['Deleted.md']),'Recovered offline text');assert.equal(strFromU8(files['Current.md']),'Newer note text');assert.ok(files['solo-recovery.json']);}
    finally{await store.close();}
});

test('a remote image refresh does not overwrite a queued local replacement',async()=>{
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),projectId=id(),imageId=id(),base=snapshot(vaultId,noteId);
    base.images=[{id:imageId,path:'photo.png',version:2}];
    await store.put('vaults',{id:vaultId,projectId,base,ready:true});await store.put('images',{id:imageId,vaultId,path:'photo.png',version:1,data:new Uint8Array([9])});
    await store.enqueue({id:id(),vaultId,projectId,created:1,command:{entity:'image',action:'update',targetId:imageId,data:new Uint8Array([9])}});
    const session=new OfflineSession(async(method,path)=>path.endsWith('/offline')?{status:200,value:base}:path.includes('/offline/documents/')?{status:200,value:{updates:[],seq:'0',seedMarkdown:null}}:{status:200,bytes:new Uint8Array([1])},{instance:'test',userId:id(),store});
    try{await session.pull(await store.get('vaults',vaultId));assert.deepEqual((await store.get('images',imageId)).data,new Uint8Array([9]));}
    finally{await session.stop();}
});

test('write acknowledgments advance deletion guards only across the writers own changes',async()=>{
    const store=new VaultStore(id()),vaultId=id(),sketchId=id(),base=snapshot(vaultId,id());await store.put('vaults',{id:vaultId,base});
    const makeDelete=()=>({id:id(),vaultId,created:2,command:{entity:'sketch',action:'delete',targetId:sketchId,version:10,baseVersions:{[sketchId]:10},baseDocuments:{[sketchId]:'1'}}});
    const write={id:id(),vaultId,documentId:sketchId,data:new Uint8Array([0,0]),created:1};const deletion=makeDelete();await store.enqueue(write);await store.enqueue(deletion);
    try{
        await store.acknowledge(write,{seq:'3',previousSeq:'2',previousVersion:11,version:12});
        let c=(await store.get('queue',deletion.id)).command;assert.equal(c.baseDocuments[sketchId],'1');assert.equal(c.version,10);
        const ownWrite={...write,id:id()};await store.enqueue(ownWrite);
        await store.acknowledge(ownWrite,{seq:'4',previousSeq:'1',previousVersion:10,version:13});
        c=(await store.get('queue',deletion.id)).command;assert.equal(c.baseDocuments[sketchId],'4');assert.equal(c.baseVersions[sketchId],13);assert.equal(c.version,13);
    }finally{await store.close();}
});

test('downloading an already edited document captures changes made while storage is opening',async()=>{
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),projectId=id(),doc=new Y.Doc();const base=snapshot(vaultId,noteId);
    await store.put('vaults',{id:vaultId,projectId,ready:true,base});await store.put('documents',{id:noteId,projectId,vaultId,state:Y.encodeStateAsUpdate(new Y.Doc()),seq:'0'});
    const client=new OfflineSession(async()=>({status:0}),{instance:'test',userId:id(),store});const original=store.all.bind(store);let release;const gate=new Promise(r=>release=r);
    store.all=async name=>{if(name==='queue')await gate;return original(name);};
    const loading=client.openDocument({documentId:noteId,projectId,vaultId},doc);doc.getText('markdown').insert(0,'Typed while downloading');release();const handle=await loading;
    try{doc.getText('markdown').insert(doc.getText('markdown').length,' and afterward');assert.equal(await handle.save(),true);const persisted=new Y.Doc();Y.applyUpdate(persisted,(await store.get('documents',noteId)).state);assert.equal(persisted.getText('markdown').toString(),'Typed while downloading and afterward');persisted.destroy();}
    finally{handle.close();doc.destroy();await client.stop();}
});

test('removing an open downloaded vault keeps the document and resumes online editing',async()=>{
    const {startOffline,offlineAction}=await import('./session.js');const {Provider}=await import('../sync/provider.js');
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),projectId=id(),base=snapshot(vaultId,noteId),sent=[],server=new Y.Doc(),updates=[];
    await store.put('vaults',{id:vaultId,projectId,ready:true,base});await store.put('documents',{id:noteId,projectId,vaultId,state:Y.encodeStateAsUpdate(new Y.Doc()),seq:'0'});
    const agent=await startOffline({invokeMethodAsync:async(method,verb,path,json)=>{
        if(method!=='Request')return;const body=json?JSON.parse(json):null;
        if(path.endsWith('/offline'))return {status:200,value:{...base,documents:{[noteId]:String(updates.length)}}};
        if(verb==='POST'&&path.endsWith('/offline/documents/'+noteId)){const previousSeq=String(updates.length);updates.push(body.data);Y.applyUpdate(server,decode(body.data));return {status:200,value:{seq:String(updates.length),previousSeq}};}
        return {status:200,value:{updates,seq:String(updates.length),seedMarkdown:null}};
    }},{instance:'test',userId:'writer',store});
    const provider=new Provider({send:async(kind,raw)=>{sent.push([kind,JSON.parse(raw)]);return 0;}},{canWrite:true,documentId:noteId,projectId,vaultId,user:{userId:'writer'}});
    try{
        await provider.connected(true);assert.ok(provider.offline);provider.doc.getText('markdown').insert(0,'Saved before removal. ');
        const result=await offlineAction('forget',null,vaultId);assert.equal(result.ok,true);assert.equal(await store.get('vaults',vaultId),undefined);assert.equal(provider.offline,null);assert.equal(server.getText('markdown').toString(),'Saved before removal. ');
        await provider.receive('synced',JSON.stringify({seq:'0',canWrite:true,updates:0,syncId:provider.syncId}));
        provider.doc.getText('markdown').insert(provider.doc.getText('markdown').length,'Still editing online');await wait(90);assert.ok(sent.some(([kind])=>kind==='update'));assert.equal((await store.all('queue')).length,0);
    }finally{provider.dispose();await agent.dispose();server.destroy();}
});

test('removing a download offline retains unsynced edits and restores the open editor',async()=>{
    const store=new VaultStore(id()),vaultId=id(),noteId=id(),projectId=id(),doc=new Y.Doc();
    await store.put('vaults',{id:vaultId,projectId,ready:true,base:snapshot(vaultId,noteId)});
    const client=new OfflineSession(async()=>({status:0}),{instance:'test',userId:id(),store});const handle=await client.openDocument({documentId:noteId,projectId,vaultId},doc);
    try{doc.getText('markdown').insert(0,'Keep this offline edit');const result=await client.forget(vaultId);assert.equal(result.ok,false);assert.match(result.error,/Sync pending changes/);assert.ok(await store.get('vaults',vaultId));assert.equal(handle.canWrite,true);assert.equal((await store.all('queue')).length,1);assert.equal(await handle.save(),true);}
    finally{handle.close();doc.destroy();await client.stop();}
});

test('an editor in another tab prevents removing its shared download',async()=>{
    const scope=id(),first=new VaultStore(scope),second=new VaultStore(scope),vaultId=id(),noteId=id(),projectId=id(),base=snapshot(vaultId,noteId);
    await first.put('vaults',{id:vaultId,projectId,ready:true,base});await first.put('documents',{id:noteId,projectId,vaultId,state:Y.encodeStateAsUpdate(new Y.Doc()),seq:'0'});
    const transport=async(method,path)=>({status:200,value:path.endsWith('/offline')?base:{updates:[],seq:'0',seedMarkdown:null}});
    const a=new OfflineSession(transport,{instance:'test',userId:'writer',store:first}),b=new OfflineSession(transport,{instance:'test',userId:'writer',store:second}),docA=new Y.Doc(),docB=new Y.Doc();
    const handleA=await a.openDocument({documentId:noteId,projectId,vaultId},docA),handleB=await b.openDocument({documentId:noteId,projectId,vaultId},docB);
    try{const result=await a.forget(vaultId);assert.equal(result.ok,false);assert.match(result.error,/another tab/);assert.ok(await first.get('vaults',vaultId));assert.equal(handleA.canWrite,true);assert.equal(handleB.canWrite,true);}
    finally{handleA.close();handleB.close();docA.destroy();docB.destroy();await a.stop();await b.stop();}
});

test('download removal and a new write cannot leave an orphaned upload queue',async()=>{
    const store=new VaultStore(id()),vaultId=id(),noteId=id();await store.put('vaults',{id:vaultId,ready:true,base:snapshot(vaultId,noteId)});
    try{
        const [removal,write]=await Promise.allSettled([store.forget(vaultId),store.enqueue({id:id(),vaultId,command:{entity:'note',action:'create',targetId:noteId}})]);
        assert.notEqual(removal.status,write.status);
        if(removal.status==='fulfilled'){assert.equal(await store.get('vaults',vaultId),undefined);assert.equal((await store.all('queue')).length,0);}else{assert.ok(await store.get('vaults',vaultId));assert.equal((await store.all('queue')).length,1);}
    }finally{await store.close();}
});
