import * as Y from 'yjs';
import {parser} from '@lezer/markdown';

export class VaultStore {
    constructor(scope,indexedDB=globalThis.indexedDB){
        if(!indexedDB)throw new Error('Offline storage is unavailable.');
        this.ready=new Promise((resolve,reject)=>{
            const request=indexedDB.open('solo-design-docs-offline-v1',1);
            request.onupgradeneeded=()=>{for(const name of ['vaults','documents','images','queue'])request.result.createObjectStore(name,{keyPath:'key'});};
            request.onsuccess=()=>resolve(request.result);request.onerror=()=>reject(request.error);
        });this.scope=scope+'|';
    }
    key(id){return this.scope+id;}
    async transaction(names,mode,action){
        const db=await this.ready;
        return new Promise((resolve,reject)=>{
            const tx=db.transaction(names,mode);let value;
            tx.oncomplete=()=>resolve(value);tx.onerror=event=>reject(tx.error??event.target.error??new Error("Local save failed."));tx.onabort=()=>reject(tx.error??new Error('Local save failed.'));
            try{value=action(Object.fromEntries(names.map(name=>[name,tx.objectStore(name)])));}catch(error){tx.abort();reject(error);}
        });
    }
    async get(name,id){let result;await this.transaction([name],'readonly',tables=>{tables[name].get(this.key(id)).onsuccess=e=>result=e.target.result;});return result;}
    async all(name){let rows;await this.transaction([name],'readonly',tables=>{tables[name].getAll().onsuccess=e=>rows=e.target.result.filter(r=>r.key.startsWith(this.scope));});return rows;}
    put(name,row){return this.transaction([name],'readwrite',t=>t[name].put({...row,key:this.key(row.id)}));}
    remove(name,id){return this.transaction([name],'readwrite',t=>t[name].delete(this.key(id)));}
    enqueue(command,image){return this.transaction(image?['queue','images']:['queue'],'readwrite',t=>{t.queue.add({...command,key:this.key(command.id)});if(image)t.images.put({...image,key:this.key(image.id)});});}
    async updateDocument(id,projectId,vaultId,updates,seq=null,local=false){
        const update=Y.mergeUpdates(updates);const packetId=crypto.randomUUID();let result;
        await this.transaction(local?['documents','queue']:['documents'],'readwrite',t=>{
            t.documents.get(this.key(id)).onsuccess=e=>{
                const old=e.target.result;result={...old,id,projectId,vaultId,key:this.key(id),state:Y.mergeUpdates(old?.state?[old.state,update]:[update]),seq:seq??old?.seq??'0'};
                t.documents.put(result);
                if(local)t.queue.add({key:this.key(packetId),id:packetId,projectId,vaultId,documentId:id,data:update,created:performance.timeOrigin+performance.now()});
            };
        });return result;
    }
    batchUpdates(vaultId){
        return this.transaction(['queue'],'readwrite',t=>{
            t.queue.getAll().onsuccess=e=>{
                const groups=new Map();
                const flush=()=>{for(const rows of groups.values())if(rows.length>1){
                    const id=crypto.randomUUID(),first=rows[0];
                    for(const row of rows)t.queue.delete(row.key);
                    t.queue.add({...first,id,key:this.key(id),data:Y.mergeUpdates(rows.map(r=>r.data))});
                }groups.clear();};
                for(const row of e.target.result.filter(r=>r.key.startsWith(this.scope)&&r.vaultId===vaultId).sort(order)){
                    if(!row.documentId||row.error){flush();continue;}
                    const rows=groups.get(row.documentId)??[];
                    if(rows.reduce((sum,r)=>sum+r.data.length,0)+row.data.length>1024*1024){flush();groups.set(row.documentId,[row]);}
                    else{rows.push(row);groups.set(row.documentId,rows);}
                }flush();
            };
        });
    }
    async acknowledge(command,value){
        await this.transaction(['queue','vaults','documents'],'readwrite',t=>{
            t.queue.get(this.key(command.id)).onsuccess=event=>{
                if(!event.target.result)return;
            t.queue.delete(this.key(command.id));
            if(command.command?.action==='create'&&value?.path){
                const c=command.command,oldPath=c.entity==='sketch'?attachmentPath(c.noteId,c.name+'.sketch'):c.image?.path;
                if(oldPath&&oldPath!==value.path)t.documents.getAll().onsuccess=e=>{
                    for(const row of e.target.result.filter(r=>r.key.startsWith(this.scope)&&r.vaultId===command.vaultId&&(!c.noteId||r.id===c.noteId))){
                        const doc=new Y.Doc();Y.applyUpdate(doc,row.state);const vector=Y.encodeStateVector(doc);
                        if(rewriteAttachment(doc,oldPath,value.path,c.noteId?oldPath.split('/').at(-1):null)){
                            row.state=Y.encodeStateAsUpdate(doc);t.documents.put(row);const id=crypto.randomUUID();
                            t.queue.add({key:this.key(id),id,projectId:row.projectId,vaultId:row.vaultId,documentId:row.id,data:Y.encodeStateAsUpdate(doc,vector),created:performance.timeOrigin+performance.now()});
                        }doc.destroy();
                    }
                };
            }
            if(command.documentId){
                t.queue.getAll().onsuccess=e=>{for(const row of e.target.result){
                    const c=row.command;if(!row.key.startsWith(this.scope)||typeof value.previousSeq!=='string'||!c?.baseDocuments||!Object.hasOwn(c.baseDocuments,command.documentId)||c.baseDocuments[command.documentId]!==value.previousSeq)continue;
                    c.baseDocuments[command.documentId]=value.seq;
                    if(value.previousVersion!=null&&c.baseVersions?.[command.documentId]===value.previousVersion){
                        c.baseVersions[command.documentId]=value.version;if(c.targetId===command.documentId&&c.version===value.previousVersion)c.version=value.version;
                    }t.queue.put(row);
                }};
                return;
            }
            t.vaults.get(this.key(command.vaultId)).onsuccess=e=>{
                const vault=e.target.result;if(!vault)return;
                applyCommand(vault.base,command.command,value);t.vaults.put(vault);
            };
            t.queue.getAll().onsuccess=e=>{
                const next=e.target.result.filter(row=>row.key.startsWith(this.scope)&&row.id!==command.id&&row.command?.targetId===command.command.targetId).sort(order)[0];
                if(value)for(const row of e.target.result)if(row.key.startsWith(this.scope)&&row.command?.baseVersions?.[command.command.targetId]!==undefined){row.command.baseVersions[command.command.targetId]=value.version;t.queue.put(row);}
                if(next&&value){if(next.command.name===command.command.name&&value.name)next.command.name=value.name;if(next.command.image&&value.path)next.command.image.path=value.path;next.command.version=value.version;next.command.baseName=value.name;next.command.baseParentId=value.parentId??value.folderId??null;t.queue.put(next);}
            };
            };
        });
    }
    async forget(vaultId){
        const queue=await this.all('queue');if(queue.some(row=>row.vaultId===vaultId))throw new Error('Sync or export pending changes before removing this download.');
        await this.transaction(['vaults','documents','images'],'readwrite',t=>{
            t.vaults.delete(this.key(vaultId));for(const name of ['documents','images'])t[name].getAll().onsuccess=e=>{for(const row of e.target.result)if(row.key.startsWith(this.scope)&&row.vaultId===vaultId)t[name].delete(row.key);};
        });
    }
    async close(){(await this.ready).close();}
}
export const order=(a,b)=>a.created-b.created||a.id.localeCompare(b.id);
export function applyCommand(manifest,c,value=null){
    const tree=manifest.tree;const groups={folder:tree.folders,note:tree.notes,sketch:manifest.sketches,image:manifest.images};
    if(c.entity==='vault'){
        if(c.action==='delete'){manifest.deleted=true;return;}
        tree.vault=value??{...tree.vault,id:c.targetId,name:c.name,isClosed:c.isClosed,version:c.version};manifest.canWrite=(manifest.projectWrite??manifest.canWrite)&&!tree.vault.isClosed;return;
    }
    const list=groups[c.entity];const index=list.findIndex(row=>row.id===c.targetId),old=list[index];
    if(c.action==='delete'){
        if(c.entity==='folder'){
            const ids=new Set([c.targetId]);let grew=true;
            while(grew){grew=false;for(const f of tree.folders)if(ids.has(f.parentId)&&!ids.has(f.id)){ids.add(f.id);grew=true;}}
            tree.folders=tree.folders.filter(f=>!ids.has(f.id));const notes=tree.notes.filter(n=>ids.has(n.folderId)).map(n=>n.id);
            tree.notes=tree.notes.filter(n=>!notes.includes(n.id));manifest.sketches=manifest.sketches.filter(s=>!notes.includes(s.noteId));
        }else{if(index>=0)list.splice(index,1);if(c.entity==='note')manifest.sketches=manifest.sketches.filter(s=>s.noteId!==c.targetId);}
        return;
    }
    const next=value??{...old,id:c.targetId,name:c.name,version:c.version??0};
    if(!value){
        if(c.entity==='folder')next.parentId=c.parentId??null;
        if(c.entity==='note')Object.assign(next,{vaultId:tree.vault.id,folderId:c.parentId??null,markdown:old?.markdown??'',seq:old?.seq??'0',headings:old?.headings??[]});
        if(c.entity==='sketch')Object.assign(next,{noteId:c.noteId??old?.noteId,pageSize:c.pageSize??old?.pageSize??'A4',orientation:c.orientation??old?.orientation??'portrait',pattern:c.pattern??old?.pattern??'empty'});
        if(c.entity==='image')Object.assign(next,c.image);
    }
    if(index<0)list.push(next);else list[index]=next;
    if(c.entity==='sketch')next.path=attachmentPath(next.noteId,next.name+'.sketch');
}
export function projectVault(vault,queue){const projected=structuredClone(vault.base);for(const row of queue.filter(r=>r.vaultId===vault.id&&r.command).sort(order))applyCommand(projected,row.command);return projected;}
export function noteKey(id){const hex=id.replaceAll('-','');return btoa(String.fromCharCode(...hex.match(/../g).map(pair=>parseInt(pair,16)))).replaceAll('+','-').replaceAll('/','_').replaceAll('=','');}
export const attachmentPath=(noteId,name)=>'attachments/'+noteKey(noteId)+'/'+name;

export function rewriteAttachment(doc,oldPath,newPath,shortName=null){
    const text=doc.getText('markdown'),markdown=text.toString(),tree=parser.parse(markdown),edits=[];
    for(const match of markdown.matchAll(/!\[\[([^\]]+)\]\]|!\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)/g)){
        let code=false;for(let node=tree.resolveInner(match.index,1);node;node=node.parent)if(/^(FencedCode|CodeBlock|InlineCode)$/.test(node.name)){code=true;break;}
        if(code)continue;
        const target=(match[1]??match[2]??match[3]).split('|')[0];let decoded;try{decoded=decodeURIComponent(target);}catch{continue;}
        if(decoded!==oldPath&&decoded!==shortName)continue;
        edits.push({at:match.index+match[0].indexOf(target),length:target.length,value:match[1]?newPath:encodeURI(newPath)});
    }
    doc.transact(()=>{for(const e of edits.reverse()){text.delete(e.at,e.length);text.insert(e.at,e.value);}});
    return edits.length>0;
}
