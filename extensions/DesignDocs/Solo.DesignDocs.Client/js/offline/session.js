import {recoveryArchive} from './recovery.js';
import * as Y from 'yjs';
import {VaultStore,projectVault,applyCommand,attachmentPath,noteKey,order} from './store.js';
import {encode,decode} from '../sync/protocol.js';
import {headings} from '../editor/markdown.js';

let session;
let markStarted,started;function resetStarted(){started=new Promise(resolve=>markStarted=resolve);}resetStarted();
async function current(){if(session)return session;return Promise.race([started,new Promise(resolve=>setTimeout(()=>resolve(null),5000))]);}
const remote=Symbol('offline remote');
const empty=()=>({tree:{vault:{},folders:[],notes:[]},sketches:[],images:[],documents:{},canWrite:true,canManage:false,projectWrite:true});
export class OfflineSession {
    constructor(transport,options){
        this.transport=transport;this.options=options;this.store=options.store??new VaultStore(options.instance+'|'+options.userId);
        this.locks=options.locks??globalThis.navigator?.locks;this.removing=new Set();this.forgetting=new Set();
        this.documents=new Map();this.active=true;this.network=true;this.message='';this.localOperations=new Set();this.removeSaveGuard=globalThis.solo?.offline?.guard(async()=>{await Promise.allSettled([...this.localOperations]);const results=await Promise.all([...this.documents.values()].map(e=>e.flush().then(()=>!e.pending.length,()=>false)));return results.every(Boolean);});
        this.wake=()=>{if(typeof document==='undefined'||!document.hidden)this.sync();};
        this.online=()=>{this.network=true;this.sync();};
        this.timer=setInterval(this.wake,10000);globalThis.addEventListener?.('online',this.online);globalThis.document?.addEventListener('visibilitychange',this.wake);
        if(typeof BroadcastChannel!=='undefined'){this.channel=new BroadcastChannel('solo-docs-'+options.instance+'|'+options.userId);this.channel.onmessage=()=>this.refreshOpenDocuments();}
    }
    async request(method,path,body=null,bytes=null){
        if(!this.active)throw new Error("Offline session ended.");
        let response;
        for(let attempt=0;;attempt++){
            response=await this.transport(method,path,body,bytes);
            if(response.status!==429||attempt>=6||!this.active)break;
            this.message='The server is busy. Pending changes remain on this device.';await this.notify();
            await new Promise(resolve=>setTimeout(resolve,Math.min(30000,response.retryAfter??1000)));
        }
        if(!this.active)throw new Error('Offline session ended.');
        if([0,502,503,504].includes(response.status)){this.network=false;throw Object.assign(new Error('The server is unavailable. Changes remain on this device.'),response);}
        this.network=true;if(response.status<200||response.status>=300)throw Object.assign(new Error(response.error??'The server refused this change.'),response);
        return bytes===true?response.bytes:response.value;
    }
    root(v){return `api/ext/design-docs/projects/${v.projectId}/vaults/${v.id}`;}
    lock(vaultId,mode,action,ifAvailable=false){return this.locks?this.locks.request('solo-docs-vault:'+this.store.key(vaultId),{mode,ifAvailable},action):action(true);}
    async hold(vaultId){
        if(!this.locks)return ()=>Promise.resolve();
        let acquired,reject;const ready=new Promise((resolve,fail)=>{acquired=resolve;reject=fail;});
        const held=this.lock(vaultId,'shared',()=>new Promise(release=>acquired(()=>{release();return held;})));
        held.catch(reject);return ready;
    }
    async view(vaultId){const v=await this.store.get('vaults',vaultId);return v?projectVault(v,await this.store.all('queue')):null;}
    async notify(){if(!this.active)return;const queue=await this.store.all('queue');const vaults=await this.store.all('vaults');const status={pending:queue.length,conflicts:queue.filter(r=>r.error).length,online:this.network,message:this.message,vaults:vaults.map(v=>({id:v.id,projectId:v.projectId,projectAlias:v.projectAlias,name:v.base.tree.vault.name,ready:v.ready,pending:queue.filter(q=>q.vaultId===v.id).length,conflicts:queue.filter(q=>q.vaultId===v.id&&q.error).length,unavailable:v.unavailable}))};if(!this.active)return;try{await this.options.changed?.(status);}catch(error){if(this.active)throw error;}}
    async pin(projectId,vaultId){
        return this.lock(vaultId,'shared',()=>this.download(projectId,vaultId));
    }
    async download(projectId,vaultId){
        if(!globalThis.solo?.offline)throw new Error("Update the Solo client to download vaults for offline use.");
        await globalThis.solo.offline.prepare(this.options.assets??[]);
        const prior=await this.store.get('vaults',vaultId);const vault=prior??{id:vaultId,projectId,base:empty(),ready:false};
        const projects=await this.request('GET','api/projects');vault.projectAlias=projects.find(p=>p.id===projectId)?.alias;vault.list=await this.request('GET',`api/ext/design-docs/projects/${projectId}/vaults`);
        await this.request('GET',`api/projects/${projectId}`);
        await this.request('GET','api/users/me');
        for(const extension of await this.request('GET','api/extensions'))if(extension.clientAssembly)await this.request('GET',`api/extensions/${extension.id}/client/${extension.clientAssembly}.dll`,null,true);
        vault.color=await this.request('POST',`api/ext/design-docs/projects/${projectId}/colors`,{});vault.colors=await this.request('GET',`api/ext/design-docs/projects/${projectId}/colors`);
        this.message='Downloading the complete vault…';await this.notify();
        try{
            await this.pull(vault,true);vault.ready=true;await this.store.put('vaults',vault);
            await globalThis.navigator?.storage?.persist?.();this.message='Vault ready offline.';
        }catch(error){this.message=error.message;if(!prior)await this.store.put('vaults',vault);throw error;}
        finally{await this.notify();}
        globalThis.dispatchEvent?.(new CustomEvent('solo-docs-offline-ready',{detail:vaultId}));this.sync();
    }
    async pull(vault,initial=false){
        let manifest;
        try{manifest=await this.request('GET',this.root(vault)+'/offline');}
        catch(error){if([403,404].includes(error.status)){vault.unavailable=true;vault.base.canWrite=false;await this.store.put('vaults',vault);this.setAccess(vault.id,false);throw Object.assign(new Error('This vault is unavailable. Pending work remains available for export.'),{status:error.status});}throw error;}
        vault.unavailable=false;
        const queued=await this.store.all('queue');
        for(const [id,seq] of Object.entries(manifest.documents)){
            const local=await this.store.get('documents',id);
            if(local?.seq===seq&&!(local.readonlySeed&&manifest.canWrite))continue;
            let received=await this.request('GET',this.root(vault)+`/offline/documents/${id}?after=${local?.seq??'0'}`);
            if(received.seedMarkdown!==null&&received.seedMarkdown!==undefined){
                const doc=new Y.Doc();doc.getText('markdown').insert(0,received.seedMarkdown);const data=Y.encodeStateAsUpdate(doc);doc.destroy();
                if(manifest.canWrite){
                    try{await this.request('POST',this.root(vault)+`/offline/documents/${id}`,{id:crypto.randomUUID(),data:encode(data),seedMarkdown:received.seedMarkdown});}
                    catch(error){if(error.status!==409)throw error;}
                    received=await this.request('GET',this.root(vault)+`/offline/documents/${id}?after=0`);
                }else received.updates=[encode(data)];
            }
            const updates=received.updates.map(decode);
            if(local?.readonlySeed&&received.seedMarkdown==null){
                await this.store.put('documents',{id,projectId:vault.projectId,vaultId:vault.id,state:Y.mergeUpdates(updates),seq:received.seq});
                const opened=this.documents.get(id);if(opened){opened.reopenRequired=true;opened.access?.(false);opened.status?.('Reopen this note to load its initialized document.');}
            }else if(updates.length)await this.store.updateDocument(id,vault.projectId,vault.id,updates,received.seq);
            else if(!local)await this.store.put('documents',{id,projectId:vault.projectId,vaultId:vault.id,state:Y.encodeStateAsUpdate(new Y.Doc()),seq:received.seq});
            if(received.seedMarkdown!==null&&received.seedMarkdown!==undefined&&!manifest.canWrite){const row=await this.store.get('documents',id);row.readonlySeed=true;await this.store.put('documents',row);}
            if(this.documents.has(id)&&updates.length&&!this.documents.get(id).reopenRequired)Y.applyUpdate(this.documents.get(id).doc,Y.mergeUpdates(updates),remote);
            if(received.updateCount>=500&&manifest.canWrite&&!queued.some(q=>q.documentId===id)&&!this.documents.get(id)?.pending.length){
                const record=await this.store.get('documents',id),snapshot=new Y.Doc();Y.applyUpdate(snapshot,record.state);
                await this.request('POST',this.root(vault)+`/offline/documents/${id}/compact`,{throughSeq:received.seq,data:encode(Y.encodeStateAsUpdate(snapshot))});snapshot.destroy();
            }
        }
        for(const image of manifest.images){
            const local=await this.store.get('images',image.id);
            if(!queued.some(q=>q.command?.entity==='image'&&q.command.targetId===image.id)&&local?.version!==image.version){const data=await this.request('GET',this.root(vault)+`/images/${image.id}`,null,true);await this.store.put('images',{...image,data,vaultId:vault.id});}
        }
        const pending=await this.store.all('queue');
        for(const record of (await this.store.all('documents')).filter(d=>d.vaultId===vault.id)){
            const deleted=!Object.hasOwn(manifest.documents,record.id)&&!pending.some(q=>q.command?.action==='create'&&q.command.targetId===record.id);
            if(record.deleted!==deleted){record.deleted=deleted;await this.store.put('documents',record);}
            const opened=this.documents.get(record.id);if(opened){opened.deleted=deleted;if(deleted){opened.access?.(false);opened.status?.('Document deleted online. Local work remains available for recovery.');}}
        }
        for(const row of pending.filter(row=>row.vaultId===vault.id&&row.documentId&&!Object.hasOwn(manifest.documents,row.documentId))){
            if(!pending.some(p=>p.command?.action==='create'&&p.command.targetId===row.documentId)){row.error='The document was deleted elsewhere. Export your local work before removing this change.';await this.store.put('queue',row);}
        }
        if(vault.base?.tree?.notes?.some(note=>!manifest.tree.notes.some(current=>current.id===note.id))){
            vault.recovery??=structuredClone(vault.base);
            for(const group of ['folders','notes'])for(const row of vault.base.tree[group])if(!vault.recovery.tree[group].some(r=>r.id===row.id))vault.recovery.tree[group].push(structuredClone(row));
            for(const row of vault.base.sketches)if(!vault.recovery.sketches.some(r=>r.id===row.id))vault.recovery.sketches.push(structuredClone(row));
        }
        vault.derived={};vault.base=manifest;await this.store.put('vaults',vault);this.setAccess(vault.id,manifest.canWrite);
    }
    async sync(){
        if(!this.active||this.running)return this.running;
        this.running=this.syncCore().catch(error=>{this.message=error.message;}).finally(async()=>{try{if(this.active)await this.notify();}finally{this.running=null;}});
        return this.running;
    }
    async syncCore(){
        const vaults=await this.store.all('vaults');if(!vaults.length)return;
        this.message='Syncing downloaded vaults…';await this.notify();
        for(const vault of vaults){
            if(!this.active)return;
            if(this.forgetting.has(vault.id))continue;
            await this.lock(vault.id,'shared',async()=>{
            if(!await this.store.get('vaults',vault.id))return;
            // Refresh access and deletion state before replaying offline writes.
            const pending=await this.store.all('queue');const creating=pending.some(r=>r.vaultId===vault.id&&r.command?.entity==='vault'&&r.command.action==='create');
            if(!creating){try{await this.pull(vault);}catch(error){if([403,404].includes(error.status))return;throw error;}}
            await this.store.batchUpdates(vault.id);
            const commands=(await this.store.all('queue')).filter(r=>r.vaultId===vault.id&&!r.error).sort(order);let sent=false;
            for(const item of commands){
                if(!this.active)return;
                try{
                    if(item.documentId){
                        const seq=await this.request('POST',this.root(vault)+`/offline/documents/${item.documentId}`,{id:item.id,data:encode(item.data)});sent=true;await this.store.acknowledge(item,seq);
                    }else{
                        const current=await this.store.get('queue',item.id);if(!current)continue;
                        const command={...current.command,id:current.id};if(command.data instanceof Uint8Array)command.data=encode(command.data);
                        const value=await this.request('POST',this.root(vault)+'/offline/commands',command);sent=true;await this.store.acknowledge(current,value);
                        if(command.entity==='vault'&&command.action==='delete'){vault.deleted=true;break;}
                    }
                }catch(error){
                    if([400,403,404,409,413,415].includes(error.status)){item.error=error.message;await this.store.put('queue',item);}
                    else throw error;
                }
            }
            if(!vault.deleted){if(sent||creating)await this.pull(vault);await this.copyNotes(vault);}
            });
        }
        this.message='';await this.refreshOpenDocuments();
    }
    async copyNotes(vault){
        if(!vault.base.canWrite)return;
        const queue=await this.store.all('queue');
        vault.copied??={};let changed=false;
        for(const note of vault.base.tree.notes){
            if(queue.some(q=>q.documentId===note.id)||vault.copied[note.id]===vault.base.documents[note.id])continue;
            const record=await this.store.get('documents',note.id);if(!record)continue;
            const doc=new Y.Doc();Y.applyUpdate(doc,record.state);const markdown=doc.getText('markdown').toString();doc.destroy();
            if(markdown!==note.markdown)await this.request('PUT',this.root(vault)+`/notes/${note.id}/copy`,{markdown,seq:record.seq,headings:headings(markdown)});
            vault.copied[note.id]=record.seq;changed=true;
        }
        if(changed)await this.store.put('vaults',vault);
    }
    setAccess(vaultId,value){for(const entry of this.documents.values())if(entry.vaultId===vaultId){entry.canWrite=value&&!this.removing.has(vaultId)&&!entry.reopenRequired&&!entry.deleted;entry.access?.(entry.canWrite);}}
    async openDocument(options,doc,access,status){
        if(!this.active||options.user?.userId&&options.user.userId!==this.options.userId)return null;
        if(this.removing.has(options.vaultId))return null;
        const releaseLock=await this.hold(options.vaultId);let entry;
        try{
        const vault=await this.store.get('vaults',options.vaultId);if(!vault?.ready)return null;
        const record=await this.store.get('documents',options.documentId);
        const projected=projectVault(vault,await this.store.all('queue'));
        if(!this.active||options.canceled?.())return null;
        let local;
        if(record){const before=new Y.Doc();Y.applyUpdate(before,record.state);local=Y.encodeStateAsUpdate(doc,Y.encodeStateVector(before));before.destroy();Y.applyUpdate(doc,record.state,remote);}
        entry={doc,id:options.documentId,vaultId:options.vaultId,projectId:options.projectId,canWrite:projected.canWrite&&!vault.unavailable&&!record?.deleted,deleted:!!record?.deleted,access,status,pending:[],writing:Promise.resolve(),releaseLock,removed:options.removed};
        const flush=()=>{
            clearTimeout(entry.timer);entry.timer=null;if(!entry.pending.length)return entry.writing;
            const updates=entry.pending.splice(0);
            entry.saving=true;entry.writing=entry.writing.catch(()=>{}).then(()=>this.store.updateDocument(entry.id,entry.projectId,entry.vaultId,updates,null,true)).then(async()=>{status?.('Saved on this device');this.channel?.postMessage(entry.id);await this.notify();this.sync();},error=>{entry.pending.unshift(...updates);status?.('Local save failed. Keep this editor open.');throw error;}).finally(()=>entry.saving=false);
            return entry.writing;
        };
        const changed=(update,origin)=>{if(origin===remote)return;entry.pending.push(update);status?.('Saving on this device…');entry.timer??=setTimeout(()=>{flush().catch(()=>{});},40);};
        doc.on('update',changed);entry.flush=flush;this.documents.set(entry.id,entry);
        entry.close=()=>{clearTimeout(entry.timer);doc.off('update',changed);if(this.documents.get(entry.id)===entry)this.documents.delete(entry.id);entry.releaseLock();};
        if(local?.length>2){entry.pending.push(local);flush().catch(()=>{});}
        return {get canWrite(){return entry.canWrite;},save:async()=>{try{await flush();return !entry.pending.length;}catch{return false;}},hasDraft:()=>!!entry.pending.length||!!entry.saving,
            apply:async update=>{Y.applyUpdate(doc,update,remote);await this.store.updateDocument(entry.id,entry.projectId,entry.vaultId,[update]);},close:entry.close,flush};
        }finally{if(!entry)await releaseLock();}
    }
    async refreshOpenDocuments(){if(!this.active)return;for(const entry of this.documents.values()){const record=await this.store.get('documents',entry.id);if(record)Y.applyUpdate(entry.doc,record.state,remote);}await this.notify();}
    async enqueue(vault,command,image=null){
        if(!this.active)throw new Error("The account changed. Reopen the vault before editing.");
        if(this.removing.has(vault.id))throw new Error('The download is being removed. Wait before editing.');
        const row={id:crypto.randomUUID(),created:performance.timeOrigin+performance.now(),projectId:vault.projectId,vaultId:vault.id,command};
        await this.store.enqueue(row,image);this.setAccess(vault.id,(await this.view(vault.id)).canWrite&&!vault.unavailable);this.channel?.postMessage(vault.id);await this.notify();this.sync();return row;
    }
    async forget(vaultId){
        if(this.removing.has(vaultId))return {ok:false,error:'The download is already being removed.'};
        const vault=await this.store.get('vaults',vaultId);if(!vault)return {ok:true};
        const entries=[...this.documents.values()].filter(e=>e.vaultId===vaultId);let released=false;
        this.removing.add(vaultId);this.setAccess(vaultId,false);
        try{
            if(await globalThis.solo?.offline?.flush?.()===false)return {ok:false,error:'Saving did not finish. Keep this vault open and try again.'};
            await Promise.allSettled([...this.localOperations]);
            const saved=await Promise.allSettled(entries.map(e=>e.flush()));
            if(saved.some(r=>r.status==='rejected')||entries.some(e=>e.pending.length))return {ok:false,error:'Saving did not finish. Keep this vault open and try again.'};
            await this.sync();
            if((await this.store.all('queue')).some(q=>q.vaultId===vaultId))return {ok:false,error:'Sync pending changes before removing this download.'};
            if(entries.length){
                if(!this.locks)return {ok:false,error:'Close this vault before removing its download in this browser.'};
                try{await this.request('GET',this.root(vault)+'/offline');}catch{return {ok:false,error:'Reconnect before removing an open vault’s download.'};}
            }
            this.forgetting.add(vaultId);await this.running;
            await Promise.all(entries.map(e=>e.releaseLock()));released=true;
            const result=await this.lock(vaultId,'exclusive',async lock=>{
                if(!lock)return {ok:false,error:'This vault is busy in another tab. Close it there and try again.'};
                await this.store.forget(vaultId);
                for(const entry of entries)if(this.documents.get(entry.id)===entry){if(entry.removed)await entry.removed();else entry.close();}
                this.message='';this.channel?.postMessage(vaultId);return {ok:true};
            },true);
            return result;
        }catch{return {ok:false,error:'Could not remove this download. Your local work is retained. Try again.'};}
        finally{
            if(released)for(const entry of entries)if(this.documents.get(entry.id)===entry)entry.releaseLock=await this.hold(vaultId);
            this.removing.delete(vaultId);this.forgetting.delete(vaultId);
            const current=await this.store.get('vaults',vaultId);if(current)this.setAccess(vaultId,(await this.view(vaultId)).canWrite&&!current.unavailable);
            await this.notify();
        }
    }
    async exportRecovery(vaultId){
        const archive=await recoveryArchive(this.store,vaultId);
        const url=URL.createObjectURL(new Blob([archive.bytes],{type:'application/zip'}));const a=document.createElement('a');a.href=url;a.download=archive.name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
    }
    async problems(vaultId){return (await this.store.all('queue')).filter(q=>q.vaultId===vaultId&&q.error).map(q=>({id:q.id,message:q.error,name:q.command?.name??q.documentId,metadata:!!q.command}));}
    async resolve(id,keepLocal){
        const row=await this.store.get('queue',id);if(!row)return;
        if(!keepLocal){await this.store.remove('queue',id);await this.refreshOpenDocuments();return;}
        if(row.command){
            const vault=await this.store.get('vaults',row.vaultId);await this.pull(vault);
            const view=vault.base,c=row.command;
            const groups={vault:[view.tree.vault],folder:view.tree.folders,note:view.tree.notes,sketch:view.sketches,image:view.images};
            const current=groups[c.entity].find(v=>v.id===c.targetId);
            if(c.action!=='create'&&!current)throw new Error('The item was deleted. Export your local work before discarding this change.');
            if(current){c.version=current.version;c.baseName=current.name;c.baseParentId=current.parentId??current.folderId??null;}
            if(c.action==='delete')throw new Error('Review the current vault and delete the item again. Export local work before discarding the queued deletion.');
        }
        delete row.error;await this.store.put('queue',row);await this.notify();this.sync();
    }

    async stop(){if(this.stopped)return;this.stopped=true;this.active=false;this.removeSaveGuard?.();for(const vaultId of new Set([...this.documents.values()].map(e=>e.vaultId)))this.setAccess(vaultId,false);clearInterval(this.timer);globalThis.removeEventListener?.('online',this.online);globalThis.document?.removeEventListener('visibilitychange',this.wake);this.channel?.close();await Promise.allSettled([...this.documents.values()].map(e=>e.flush()));await Promise.allSettled([...this.localOperations]);for(const entry of this.documents.values())entry.close();await this.running;await this.store.close();}
}
export async function startOffline(bridge,options){
    if(session)await session.stop();
    session=new OfflineSession(async(method,path,body,bytes)=>bridge.invokeMethodAsync('Request',method,path,body?JSON.stringify(body):null,bytes===true,bytes instanceof Uint8Array?bytes:null),{...options,changed:value=>bridge.invokeMethodAsync('Changed',value)});
    const active=session;markStarted(active);await active.notify();active.sync();return {dispose:async()=>{await active.stop();if(session===active){session=null;resetStarted();}},sync:()=>active.sync()};
}
export const offlineDocument=async(options,doc,access,status)=>(await current())?.openDocument(options,doc,access,status)??null;
export const offlineAction=async(action,projectId,vaultId)=>{if(!session)throw new Error('The offline service is not ready.');if(action==='pin')return session.pin(projectId,vaultId);if(action==='forget')return session.forget(vaultId);if(action==='export')return session.exportRecovery(vaultId);if(action==='problems')return session.problems(vaultId);if(action==='retry')return session.resolve(vaultId,true);if(action==='discard')return session.resolve(vaultId,false);};
export const offlineStatus=async vaultId=>{const vault=await session?.store.get('vaults',vaultId);return {available:!!globalThis.solo?.offline,ready:!!vault?.ready};};

export async function offlineRequest(...args){
    const active=await current();if(!active?.active)return {handled:false};
    const operation=localRequest(active,...args);active.localOperations.add(operation);
    try{return await operation;}finally{active.localOperations.delete(operation);}
}
async function localRequest(active,method,path,json=null,bytes=null,contentType=null){
    if(!active?.active)return {handled:false};
    path=path.replace(/^\//,'');const url=new URL(path,'https://offline.invalid/');const parts=url.pathname.split('/').filter(Boolean),body=json?JSON.parse(json):null;
    if(parts[0]!=='api'||parts[1]!=='ext'||parts[2]!=='design-docs')return {handled:false};
    if(method==='GET'&&/^api\/ext\/design-docs\/notes\//.test(path)&&parts.at(-1)==='locate'){
        const vaults=await active.store.all('vaults'),queue=await active.store.all('queue');
        const views=vaults.filter(v=>v.ready).map(v=>({vault:v,view:projectVault(v,queue)}));
        const id=parts.at(-2);const found=views.find(v=>!v.view.deleted&&v.view.tree.notes.some(n=>n.id===id));
        if(found)return {handled:true,value:{projectId:found.vault.projectId,projectAlias:found.vault.projectAlias,vaultId:found.vault.id,noteId:id}};
    }
    if(method==='POST'&&url.pathname==='/api/ext/design-docs/links/labels'){
        const vaults=await active.store.all('vaults'),queue=await active.store.all('queue');
        const views=vaults.filter(v=>v.ready).map(v=>({vault:v,view:projectVault(v,queue)}));
        if(!views.length)return {handled:false};
        const labels=Object.create(null);for(const written of body.urls){const [target,heading]=written.split('#',2);
            const note=views.filter(v=>!v.view.deleted).flatMap(v=>v.view.tree.notes).find(n=>target==='/docs/note/'+noteKey(n.id)||target==='/docs/note/'+n.id||target==='/docs/note/'+n.id.replaceAll('-',''));
            if(note){const h=note.headings?.find(h=>h.slug===heading);labels[written]=h?note.name+' › '+h.text:note.name;}
        }
        const missing=body.urls.filter(u=>!labels[u]);if(missing.length)try{Object.assign(labels,await active.request('POST',path,{urls:missing}));}catch{}
        return {handled:true,value:labels};
    }
    if(parts[3]==='projects'&&parts[5]==='colors'){
        const vaults=await active.store.all('vaults');
        const cached=vaults.find(v=>v.ready&&v.projectId===parts[4]);
        return cached?{handled:true,value:method==='POST'?cached.color:cached.colors??[]}:{handled:false};
    }
    if(parts[3]==='projects'&&method==='GET'&&parts[5]==='documents'){
        const document=await active.store.get('documents',parts[6]);
        if(document?.projectId===parts[4]&&(await active.store.get('vaults',document.vaultId))?.ready)return {handled:true,value:{updates:[{data:encode(document.state),seq:document.seq}]}};
        return {handled:false};
    }
    if(parts[0]!=='api'||parts[1]!=='ext'||parts[2]!=='design-docs'||parts[3]!=='projects'||parts[5]!=='vaults')return {handled:false};
    const projectId=parts[4],vaultId=parts[6];
    if(!vaultId){
        const vaults=await active.store.all('vaults');
        if(method==='GET'){
            let value;
            try{if(!active.network||await globalThis.solo?.offline?.isOffline())throw new Error('Offline');value=await active.request('GET',path);for(const v of vaults.filter(v=>v.projectId===projectId)){v.list=value;await active.store.put('vaults',v);}}
            catch{value=vaults.find(v=>v.projectId===projectId)?.list;if(!value)return {handled:false};}
            const queue=await active.store.all('queue'),local=vaults.filter(v=>v.ready&&v.projectId===projectId).map(v=>({vault:v,view:projectVault(v,queue)})),ids=new Set(local.map(v=>v.vault.id));
            return {handled:true,value:{canManage:value.canManage,vaults:value.vaults.filter(v=>!ids.has(v.id)).concat(local.filter(v=>!v.view.deleted).map(v=>v.view.tree.vault))}};
        }
        if(method==='POST'&&vaults.some(v=>v.projectId===projectId&&v.ready)){
            if(!vaults.some(v=>v.projectId===projectId&&v.base.canManage))throw new Error('Only an administrator can create a design doc.');
            const id=crypto.randomUUID(),base=empty();base.tree.vault={id,name:body.name,isClosed:false,closedAt:null,version:0};base.canManage=true;
            const template=vaults.find(v=>v.projectId===projectId);base.canWrite=base.projectWrite=template.base.projectWrite??template.base.canWrite;const vault={id,projectId,projectAlias:template.projectAlias,base,ready:true,list:template.list,color:template.color,colors:template.colors};await active.store.put('vaults',vault);
            await active.enqueue(vault,{entity:'vault',action:'create',targetId:id,name:body.name});return {handled:true,value:base.tree.vault};
        }
        return {handled:false};
    }
    const vault=await active.store.get('vaults',vaultId);if(!vault?.ready||vault.projectId!==projectId)return {handled:false};
    const queue=await active.store.all('queue');
    const view=projectVault(vault,queue),tree=view.tree,kind=parts[7],id=parts[8];
    if(method==='GET'){
        if(!kind){for(const n of tree.notes){const derived=vault.derived?.[n.id];if(derived)Object.assign(n,derived);}return {handled:true,value:tree};}
        if(kind==='images'&&!id)return {handled:true,value:view.images};
        if(kind==='sketches'&&!id)return {handled:true,value:view.sketches};
        if(kind==='images'&&id){const image=await active.store.get('images',id);if(!image)throw new Error('The image has not finished downloading.');return {handled:true,bytes:image.data,contentType:image.contentType};}
        if(kind==='notes'&&parts[9]==='rename-impact')return {handled:true,value:{references:tree.notes.filter(n=>n.markdown?.includes('[[')).length}};
        if(kind==='notes'&&id){
            const note=tree.notes.find(n=>n.id===id);if(!note)throw new Error('This note is unavailable.');
            const record=await active.store.get('documents',id);if(!record)return {handled:true,value:note};
            const doc=new Y.Doc();Y.applyUpdate(doc,record.state);const markdown=doc.getText('markdown').toString();doc.destroy();
            return {handled:true,value:{...note,markdown,headings:headings(markdown),seq:record.seq}};
        }
        return {handled:false};
    }
    if(kind==='notes'&&parts[9]==='copy'){
        const note=vault.base.tree.notes.find(n=>n.id===id);if(note){note.markdown=body.markdown;note.headings=body.headings;}vault.derived??={};vault.derived[id]={markdown:body.markdown,headings:body.headings};await active.store.put('vaults',vault);
        return {handled:true,value:null};
    }
    if(!kind&&!view.canManage)throw new Error('Only an administrator can manage a design doc.');
    if(kind&&(!view.canWrite||vault.unavailable||tree.vault.isClosed))throw new Error('This vault is read-only. Your local changes remain available for export.');
    if(kind==='import'&&id==='notes'&&bytes){
        const imported=JSON.parse(new TextDecoder().decode(bytes)).notes;const root=active.root(vault);
        for(const note of imported){
            const segments=note.path.split('/');let parentId=null;
            for(const name of segments.slice(0,-1)){
                const current=await active.view(vaultId);let folder=current.tree.folders.find(f=>f.parentId===parentId&&f.name===name);
                if(!folder)folder=(await localRequest(active,'POST',root+'/folders',JSON.stringify({name,parentId}))).value;
                parentId=folder.id;
            }
            const created=(await localRequest(active,'POST',root+'/notes',JSON.stringify({name:segments.at(-1).replace(/\.md$/i,''),parentId}))).value;
            const doc=new Y.Doc();doc.getText('markdown').insert(0,note.markdown);
            await active.store.updateDocument(created.id,projectId,vaultId,[Y.encodeStateAsUpdate(doc)],null,true);doc.destroy();
        }
        await active.notify();active.sync();return {handled:true,value:{imported:imported.length}};
    }
    let command,value,image;
    if(!kind){command={entity:'vault',targetId:vaultId,action:method==='DELETE'?'delete':'update',version:body?.version??Number(url.searchParams.get('version')),name:body?.name,isClosed:body?.isClosed};}
    else if(kind==='folders'||kind==='notes'){
        if(!['POST','PUT','DELETE'].includes(method))return {handled:false};
        const entity=kind==='folders'?'folder':'note',targetId=method==='POST'?crypto.randomUUID():id,old=(entity==='folder'?tree.folders:tree.notes).find(n=>n.id===targetId);
        if(method!=='POST'&&!old)throw new Error('The item no longer exists.');
        command={entity,action:method==='POST'?'create':method==='PUT'?'update':'delete',targetId,name:body?.name,parentId:body?.parentId??null,version:body?.version??Number(url.searchParams.get('version')),baseName:old?.name,baseParentId:old?.parentId??old?.folderId??null};
        if(entity==='folder'&&command.action==='update'){
            for(let parent=command.parentId;parent;parent=tree.folders.find(f=>f.id===parent)?.parentId)if(parent===targetId)throw new Error('A folder cannot contain itself.');
        }
    }else if(kind==='sketches'){
        const creating=method==='POST'&&id==='notes',old=view.sketches.find(s=>s.id===id);
        if(!creating&&!old)throw new Error('The sketch no longer exists.');
        command={entity:'sketch',action:creating?'create':method==='DELETE'?'delete':'update',targetId:creating?crypto.randomUUID():id,noteId:creating?parts[9]:old?.noteId,...body};
        // A delete carries its version in the query, as online.
        if(method==='DELETE')command.version=Number(url.searchParams.get('version'));
        if(creating)command.path=attachmentPath(command.noteId,command.targetId+'/'+command.name+'.sketch');
    }else if(kind==='images'&&id&&method==='DELETE'){
        const old=view.images.find(i=>i.id===id);if(!old)throw new Error('The image no longer exists.');
        command={entity:'image',action:'delete',targetId:id,version:Number(url.searchParams.get('version'))};
    }else if(kind==='images'&&bytes){
        const creating=method==='POST'&&(!id||id==='notes'||id==='import'),old=view.images.find(i=>i.id===id),targetId=creating?crypto.randomUUID():id;
        if(!creating&&!old)return {handled:false};
        let name=creating?url.searchParams.get('path'):old.path.split('/').at(-1);
        const path=creating?(id==='notes'?attachmentPath(parts[9],targetId+'/'+name):'offline/'+targetId+'/'+name):old.path;
        command={entity:'image',action:creating?'create':'update',targetId,noteId:creating&&id==='notes'?parts[9]:null,name,version:creating?0:Number(url.searchParams.get('version')),data:bytes,
            image:{id:targetId,path,contentType,width:0,height:0,bytes:bytes.length,version:0}};
        image={...command.image,data:bytes,vaultId};
    }else return {handled:false};
    if(command.action==='delete'&&['vault','folder','note','sketch'].includes(command.entity)){
        const removed=structuredClone(view);applyCommand(removed,command);
        const allRows=m=>m.tree.folders.concat(m.tree.notes,m.sketches,m.images);
        const kept=new Set(command.entity==='vault'?[]:allRows(removed).map(row=>row.id));
        command.baseVersions=Object.fromEntries(allRows(view).filter(row=>!kept.has(row.id)).map(row=>[row.id,row.version]));
        command.baseDocuments={};for(const row of tree.notes.concat(view.sketches).filter(row=>!kept.has(row.id)))command.baseDocuments[row.id]=(await active.store.get('documents',row.id))?.seq??'0';
    }
    if(command.action!=='delete'&&command.entity!=='image'&&(!command.name?.trim()||command.name.length>200||['.','..'].includes(command.name.trim())||/[\x00-\x1f\x7f/\\:#\[\]|]/.test(command.name)))throw new Error('Use a name of up to 200 characters without path separators or Markdown link characters.');
    const projected=structuredClone(view);applyCommand(projected,command);const group={folder:projected.tree.folders,note:projected.tree.notes,sketch:projected.sketches,image:projected.images};
    value=command.entity==='vault'?projected.tree.vault:group[command.entity]?.find(v=>v.id===command.targetId)??null;
    if(command.action==='create'&&['folder','note','sketch'].includes(command.entity)){
        const existing=command.entity==='folder'?tree.folders:command.entity==='note'?tree.notes:view.sketches;
        if(existing.some(v=>v.name===command.name&&(v.parentId??v.folderId??v.noteId??null)===(command.parentId??command.noteId??null)))throw new Error('That name is already in use in this folder or note.');
        if(command.entity!=='folder')await active.store.put('documents',{id:command.targetId,projectId,vaultId,state:Y.encodeStateAsUpdate(new Y.Doc()),seq:'0'});
    }
    await active.enqueue(vault,command,image);return {handled:true,value};
}
