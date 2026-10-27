import {offlineDocument} from '../offline/session.js';
import * as Y from 'yjs';
import { Awareness, encodeAwarenessUpdate, applyAwarenessUpdate, removeAwarenessStates } from 'y-protocols/awareness';
import { encode, decode, parts, sequence, unpackUpdates } from './protocol.js';

const disposable=kind=>kind==='awareness'||kind==='closed';
export class Provider {
    constructor(bridge, options = {}) {
        this.bridge = bridge; this.options = options; this.doc = new Y.Doc(); this.awareness = new Awareness(this.doc);
        this.remote = {}; this.pending = new Map(); this.chunks = new Map(); this.seq = '0'; this.batch = [];this.loading=[];this.checkpoint='0';this.epoch=0;this.waiters=new Set();
        this.online = false; this.synced = false; this.canWrite = false; this.disposed = false; this.queue = Promise.resolve();
        this.doc.on('update', (update, origin) => {
            if (this.disposed || this.hydrating || this.offline || origin === this.remote) return;
            this.batch.push(update); clearTimeout(this.copyTimer);
            this.copyTimer = setTimeout(() => { this.copyWanted = true; this.copy(); }, 2000);
            this.batchTimer ??= setTimeout(() => { this.batchTimer = null; this.flush(); }, 50);
        });
        this.awareness.setLocalState(null);
        this.awareness.on('update', (_, origin) => {
            if (this.disposed || origin === this.remote || !this.canWrite) return;
            // A debounce prevents presence updates during continuous drawing.
            this.awarenessTimer ??= setTimeout(() => {this.awarenessTimer=null;this.send('awareness', {data: encode(encodeAwarenessUpdate(this.awareness, [this.doc.clientID]))}, this.awareness.getLocalState());}, 60);
        });
        this.offlineReady=event=>{if(!this.disposed&&event.detail===this.options.vaultId&&!this.offline)this.bindOffline().then(()=>this.connected(this.online));};
        if(options.documentId){this.hydrating=true;this.offlineLoading=this.bindOffline();globalThis.addEventListener?.('solo-docs-offline-ready',this.offlineReady);}
        this.beforeUnload = e => { if (this.hasPending || this.options.hasDraft?.()) { e.preventDefault(); e.returnValue = ''; } };
        if (typeof window !== 'undefined') window.addEventListener('beforeunload', this.beforeUnload);
        this.visibility = () => { if (!document.hidden && this.online) this.connected(true); };
        if (typeof document !== 'undefined') document.addEventListener('visibilitychange', this.visibility);
    }
    async bindOffline(){
        try{
            if(this.disposed)return;
            const binding=await offlineDocument({...this.options,canceled:()=>this.disposed,removed:()=>this.releaseOffline()},this.doc,value=>{if(!this.disposed)this.setWritable(value);},this.options.status);
            if(this.disposed){binding?.close();return;}
            this.offline=binding;
            if(this.offline){this.pending.clear();this.batch=[];clearTimeout(this.ackRetry);}
        }finally{this.hydrating=false;}
    }
    async releaseOffline(){
        this.offline?.close();this.offline=null;this.offlineLoading=null;this.checkpoint='0';this.seq='0';
        if(!this.disposed)await this.connected(this.online);
    }
    get hasPending(){return this.offline?this.offline.hasDraft():this.pending.size>0||this.batch.length>0;}
    send(kind, value, state) {
        const epoch=this.epoch;
        if(kind==='awareness'){
            if(!this.online||this.disposed)return this.queue;
            this.latestAwareness=value;this.latestAwarenessState=state;
            if(this.awarenessQueued)return this.queue;
            this.awarenessQueued=true;
        }
        this.queue = this.queue.catch(() => {}).then(async () => {
            if(epoch!==this.epoch)return;
            if(kind==='awareness'){value=this.latestAwareness;state=this.latestAwarenessState;this.awarenessQueued=false;this.latestAwareness=this.latestAwarenessState=null;}
            if(!this.online||this.disposed)return;
            let result = await this.bridge.send(kind, JSON.stringify(value));
            if(epoch!==this.epoch||this.disposed)return;
            // Presence and close notices are disposable; document writes remain pending until acknowledged.
            if (disposable(kind) && result !== 0) return;
            if (kind === 'awareness') this.options.awarenessSent?.(state);
            if (result === 4) { await new Promise(r => setTimeout(r, 100)); if (this.online&&epoch===this.epoch) result = await this.bridge.send(kind, JSON.stringify(value)); }
            if(epoch!==this.epoch||this.disposed)return;
            if (result !== 0) this.interrupted();
            await new Promise(r => setTimeout(r, 25));
        }).catch(() => {if(epoch===this.epoch&&!disposable(kind)&&!this.disposed)this.interrupted();});
        return this.queue;
    }
    interrupted(){
        this.setWritable(false);this.synced=false;
        this.options.status?.('Sync interrupted; edits retained…');clearTimeout(this.retry);
        this.retry=setTimeout(()=>{if(this.online)this.connected(true);},1500);
    }
    flush() {
        clearTimeout(this.batchTimer);this.batchTimer=null;
        if(this.offline||!this.batch.length||this.pending.size||!this.synced||!this.canWrite)return;
        let size=0,count=0;
        do{size+=this.batch[count++].byteLength;}while(count<this.batch.length&&size+this.batch[count].byteLength<=1024*1024);
        const packet={id:crypto.randomUUID(),data:encode(Y.mergeUpdates(this.batch.splice(0,count))),seedToken:this.seedToken};this.edited=true;
        this.pending.set(packet.id,packet);this.options.status?.('Saving…');this.transmit(packet);
    }
    transmit(packet) {
        const chunks=parts(packet.data);
        if(chunks.length===1)this.send('update',packet);
        else chunks.forEach((data,index)=>this.send('update-part',{...packet,data,index,count:chunks.length}));
        const epoch=this.epoch;
        return this.queue.then(()=>{
            if(this.disposed||epoch!==this.epoch||!this.online||!this.synced||this.pending.get(packet.id)!==packet)return;
            clearTimeout(this.ackRetry);
            this.ackRetry=setTimeout(()=>this.transmit(packet),5000);
        });
    }

    async connected(value) {
        if(this.offlineLoading)await this.offlineLoading;
        if(this.disposed)return;
        if(this.offline){this.online=value;this.synced=true;this.setWritable(this.offline.canWrite);this.options.status?.(this.canWrite?'Saved on this device':'Read-only · downloaded vault');return;}

        this.online=value;this.synced=false;this.epoch++;this.queue=Promise.resolve();
        this.awarenessQueued=false;this.latestAwareness=null;clearTimeout(this.ackRetry);clearTimeout(this.retry);
        this.setWritable(false);this.loading=[];this.chunks.clear();
        this.syncId=crypto.randomUUID();
        this.options.status?.(value?'Synchronizing…':'Offline · changes retained');
        if(value)this.send('sync',{afterSeq:this.checkpoint,batch:true,syncId:this.syncId});
    }
    waitForSave(timeout=10000){
        if(this.offline)return this.offline.save();
        this.flush();
        if(!this.hasPending)return Promise.resolve(true);
        return new Promise(resolve=>{
            const waiter={finish:value=>{clearTimeout(waiter.timer);this.waiters.delete(waiter);resolve(value);}};
            waiter.timer=setTimeout(()=>waiter.finish(false),Math.max(0,timeout));this.waiters.add(waiter);
        });
    }
    // Viewers' previews of a sketch refresh when an editor that changed it closes, not per stroke.
    closed(){
        if(this.offline||!this.edited||!this.online||this.disposed)return Promise.resolve();
        this.edited=false;return this.send('closed',{});
    }
    saved(){if(!this.hasPending)for(const waiter of [...this.waiters])waiter.finish(true);}
    compact(){
        if(this.offline||!this.canWrite||this.hasPending||!(this.updates>=500)||this.compactedThrough===this.checkpoint)return;
        this.compactedThrough=this.checkpoint;
        const data=encode(Y.encodeStateAsUpdate(this.doc)),chunks=parts(data),id=crypto.randomUUID();
        if(chunks.length===1)this.send('compact',{uptoSeq:this.checkpoint,data});
        else chunks.forEach((data,index)=>this.send('compact-part',{id,uptoSeq:this.checkpoint,index,count:chunks.length,data}));
    }
    setWritable(value) {
        this.canWrite = value && (this.online||!!this.offline) && !!this.options.canWrite;
        // setLocalStateField is a no-op after disconnect cleared the local state.
        if (this.canWrite) this.awareness.setLocalState({...this.awareness.getLocalState(),user:this.options.user});
        else this.awareness.setLocalState(null);
        this.options.writable?.(this.canWrite);
    }
    async receive(kind, raw) {
        if (!raw || this.disposed) return;
        const p = JSON.parse(raw);
        if (kind === 'state' || kind === 'state-batch' || kind === 'update') {
            if(p.syncId&&p.syncId!==this.syncId)return;
            if (!Number.isInteger(p.count) || p.count < 1 || p.count > 512 || p.index < 0 || p.index >= p.count) return;
            const key = `${kind}:${p.seq}`; let item = this.chunks.get(key);
            if (!item) { if (this.chunks.size > 512) this.chunks.clear(); item = {values: new Array(p.count), at: Date.now()}; this.chunks.set(key, item); }
            item.values[p.index] = p.data;
            if (Array.from(item.values).some(v => v === undefined)) return;
            const bytes=decode(item.values.join(''));this.chunks.delete(key);
            const update=kind==='state-batch'?Y.mergeUpdates(unpackUpdates(bytes)):bytes;
            if(this.offline){await this.offline.apply(update);return;}
            // Apply sketch history together to prevent visible replay.
            if(this.options.atomicSync&&!this.synced)this.loading.push(update);
            else{Y.applyUpdate(this.doc,update,this.remote);this.seq=sequence(this.seq,p.seq);}
        } else if (kind === 'synced') {
            if(this.offline)return;
            if(p.syncId&&p.syncId!==this.syncId)return;
            if([...this.chunks.keys()].some(key=>key.startsWith('state'))){this.interrupted();return;}
            if(this.loading.length){Y.applyUpdate(this.doc,Y.mergeUpdates(this.loading),this.remote);this.loading=[];}
            // A write acknowledgment can skip unseen collaborator updates; only a completed replay is a checkpoint.
            this.seq=sequence(this.seq,p.seq);this.checkpoint=p.seq;this.updates=p.updates;
            this.synced=true;this.setWritable(p.canWrite);
            if(this.pending.size)for(const packet of this.pending.values())this.transmit(packet);else this.flush();
            this.options.status?.(this.hasPending?'Saving…':this.canWrite?'Saved online':'Read-only');
            this.compact();this.copy();this.saved();
        } else if (kind === 'ack') {
            if(!this.pending.delete(p.id))return;
            clearTimeout(this.ackRetry);this.seq=sequence(this.seq,p.seq);this.seedToken=null;
            this.flush();this.copy();this.compact();this.saved();
            this.options.status?.(this.hasPending?'Saving…':'Saved online');
        } else if (kind === 'seed') {
            this.seedToken = p.seedToken; this.synced = true;
            if (!this.pending.size && !this.doc.getText('markdown').length) this.doc.getText('markdown').insert(0, p.markdown);
            const seedUpdate=Y.mergeUpdates(this.batch.splice(0));
            if(!this.pending.size&&seedUpdate.length>2){const packet={id:crypto.randomUUID(),data:encode(seedUpdate),seedToken:p.seedToken};this.pending.set(packet.id,packet);this.transmit(packet);}
            // An empty seed still needs a Yjs update to relinquish the lease.
            if (!this.pending.size) { const packet = {id: crypto.randomUUID(),data: encode(Y.encodeStateAsUpdate(this.doc)),seedToken:p.seedToken};this.pending.set(packet.id,packet);this.transmit(packet); }
            this.send('sync', {afterSeq:'0',batch:true,syncId:this.syncId});
        } else if (kind === 'seeding') {
            clearTimeout(this.retry); this.retry = setTimeout(() => this.send('sync', {afterSeq:'0',batch:true,syncId:this.syncId}), 1000);
        } else if (kind === 'unseeded') this.options.unseeded?.(p.markdown);
        else if (kind === 'awareness') applyAwarenessUpdate(this.awareness, decode(p.data), this.remote);
        else if (kind === 'refused') {
            this.setWritable(false); this.options.status?.(`Read-only · ${p.reason}`);
            if (p.reason === 'seed-expired') this.options.status?.('Seed lease expired. Reload this note before editing.');
        }
    }
    copy() {
        if (this.offline || !this.copyWanted || !this.synced || this.hasPending || !this.canWrite) return;
        this.copyWanted = false; this.options.copy?.(this.doc.getText('markdown').toString(), this.seq);
    }
    presence(users) {
        const held = new Set(users.map(u => u.userId));
        const joined=[...held].some(id=>id!==this.options.user?.userId&&!this.presentUsers?.has(id));
        this.presentUsers=held;
        if(joined&&this.canWrite){this.options.peerJoined?.();const state=this.awareness.getLocalState();if(state)this.awareness.setLocalState({...state});}
        const gone = [...this.awareness.getStates()].filter(([id, state]) => id !== this.doc.clientID && !held.has(state.user?.userId)).map(([id]) => id);
        removeAwarenessStates(this.awareness, gone, this.remote);
    }
    dispose() {
        if (this.disposed) return;
        this.disposed = true; clearTimeout(this.batchTimer); clearTimeout(this.copyTimer); clearTimeout(this.retry);clearTimeout(this.awarenessTimer);clearTimeout(this.ackRetry);
        for(const waiter of [...this.waiters])waiter.finish(false);
        if (typeof window !== 'undefined') window.removeEventListener('beforeunload', this.beforeUnload);
        if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', this.visibility);
        globalThis.removeEventListener?.('solo-docs-offline-ready',this.offlineReady);this.offline?.close();
        this.awareness.destroy(); this.doc.destroy(); this.chunks.clear();this.loading=[];
    }
}
