import * as Y from 'yjs';
import { Awareness, encodeAwarenessUpdate, applyAwarenessUpdate, removeAwarenessStates } from 'y-protocols/awareness';
import { encode, decode, parts, sequence } from './protocol.js';

export class Provider {
    constructor(bridge, options = {}) {
        this.bridge = bridge; this.options = options; this.doc = new Y.Doc(); this.awareness = new Awareness(this.doc);
        this.remote = {}; this.pending = new Map(); this.chunks = new Map(); this.seq = '0'; this.batch = [];
        this.online = false; this.synced = false; this.canWrite = false; this.disposed = false; this.queue = Promise.resolve();
        this.doc.on('update', (update, origin) => {
            if (this.disposed || origin === this.remote) return;
            this.batch.push(update); clearTimeout(this.copyTimer);
            this.copyTimer = setTimeout(() => { this.copyWanted = true; this.copy(); }, 2000);
            this.batchTimer ??= setTimeout(() => { this.batchTimer = null; this.flush(); }, 50);
        });
        this.awareness.setLocalState(null);
        this.awareness.on('update', (_, origin) => {
            if (this.disposed || origin === this.remote || !this.canWrite) return;
            clearTimeout(this.awarenessTimer);
            this.awarenessTimer = setTimeout(() => this.send('awareness', {data: encode(encodeAwarenessUpdate(this.awareness, [this.doc.clientID]))}), 60);
        });
        this.beforeUnload = e => { if (this.pending.size || this.batch.length) { e.preventDefault(); e.returnValue = ''; } };
        if (typeof window !== 'undefined') window.addEventListener('beforeunload', this.beforeUnload);
        this.visibility = () => { if (!document.hidden && this.online) this.connected(true); };
        if (typeof document !== 'undefined') document.addEventListener('visibilitychange', this.visibility);
    }
    send(kind, value) {
        this.queue = this.queue.catch(() => {}).then(async () => {
            if (!this.online || this.disposed) return;
            let result = await this.bridge.send(kind, JSON.stringify(value));
            if (result === 4) { await new Promise(r => setTimeout(r, 100)); if (this.online) result = await this.bridge.send(kind, JSON.stringify(value)); }
            if (result !== 0) { this.setWritable(false); this.options.status?.('Sync interrupted; reconnecting…'); clearTimeout(this.retry); this.retry = setTimeout(() => { if (this.online) this.connected(true); }, 1500); }
            await new Promise(r => setTimeout(r, 25));
        });
        this.queue = this.queue.catch(() => {
            this.setWritable(false); this.options.status?.("Connection interrupted · edits retained");
            clearTimeout(this.retry); this.retry = setTimeout(() => { if (this.online) this.connected(true); }, 1500);
        });
        return this.queue;
    }
    flush() {
        clearTimeout(this.batchTimer); this.batchTimer = null;
        if (!this.batch.length) return;
        const packet = {id: crypto.randomUUID(), data: encode(Y.mergeUpdates(this.batch)), seedToken: this.seedToken};
        this.batch = []; this.pending.set(packet.id, packet); if (this.synced) this.transmit(packet);
    }
    transmit(packet) {
        const chunks = parts(packet.data);
        if (chunks.length === 1) this.send('update', packet);
        else chunks.forEach((data, index) => this.send('update-part', {...packet, data, index, count: chunks.length}));
    }
    connected(value) {
        this.online = value; this.synced = false; this.setWritable(false);
        this.options.status?.(value ? 'Synchronizing…' : 'Offline · read-only');
        if (value) this.send('sync', {afterSeq: this.seq});
    }
    setWritable(value) {
        this.canWrite = value && this.online && !!this.options.canWrite;
        if (this.canWrite) this.awareness.setLocalStateField('user', this.options.user);
        else this.awareness.setLocalState(null);
        this.options.writable?.(this.canWrite);
    }
    async receive(kind, raw) {
        if (!raw || this.disposed) return;
        const p = JSON.parse(raw);
        if (kind === 'state' || kind === 'update') {
            if (!Number.isInteger(p.count) || p.count < 1 || p.count > 512 || p.index < 0 || p.index >= p.count) return;
            const key = `${kind}:${p.seq}`; let item = this.chunks.get(key);
            if (!item) { if (this.chunks.size > 512) this.chunks.clear(); item = {values: new Array(p.count), at: Date.now()}; this.chunks.set(key, item); }
            item.values[p.index] = p.data;
            if (Array.from(item.values).some(v => v === undefined)) return;
            Y.applyUpdate(this.doc, decode(item.values.join('')), this.remote); this.chunks.delete(key); this.seq = sequence(this.seq, p.seq);
        } else if (kind === 'synced') {
            this.seq = sequence(this.seq, p.seq); this.synced = true; this.setWritable(p.canWrite);
            this.options.status?.(this.canWrite ? 'Saved online' : 'Read-only');
            this.flush(); for (const packet of this.pending.values()) this.transmit(packet);
            if (this.canWrite && p.updates >= 500 && !this.pending.size) {
                const data = encode(Y.encodeStateAsUpdate(this.doc)), chunks = parts(data), id = crypto.randomUUID();
                if (chunks.length === 1) this.send('compact', {uptoSeq:p.seq, data});
                else chunks.forEach((data,index) => this.send('compact-part', {id,uptoSeq:p.seq,index,count:chunks.length,data}));
            }
            this.copy();
        } else if (kind === 'ack') {
            this.pending.delete(p.id); this.seq = sequence(this.seq, p.seq); this.seedToken = null; this.copy();
            this.options.status?.(this.pending.size ? 'Saving…' : 'Saved online');
        } else if (kind === 'seed') {
            this.seedToken = p.seedToken; this.synced = true;
            if (!this.pending.size && !this.doc.getText('markdown').length) this.doc.getText('markdown').insert(0, p.markdown);
            this.flush();
            // An empty seed still needs a Yjs update to relinquish the lease.
            if (!this.pending.size) { const packet = {id: crypto.randomUUID(),data: encode(Y.encodeStateAsUpdate(this.doc)),seedToken:p.seedToken};this.pending.set(packet.id,packet);this.transmit(packet); }
            this.send('sync', {afterSeq:'0'});
        } else if (kind === 'seeding') {
            clearTimeout(this.retry); this.retry = setTimeout(() => this.send('sync', {afterSeq:'0'}), 1000);
        } else if (kind === 'unseeded') this.options.unseeded?.(p.markdown);
        else if (kind === 'awareness') applyAwarenessUpdate(this.awareness, decode(p.data), this.remote);
        else if (kind === 'refused') {
            this.setWritable(false); this.options.status?.(`Read-only · ${p.reason}`);
            if (p.reason === 'seed-expired') this.options.status?.('Seed lease expired. Reload this note before editing.');
        }
    }
    copy() {
        if (!this.copyWanted || !this.synced || this.pending.size || !this.canWrite) return;
        this.copyWanted = false; this.options.copy?.(this.doc.getText('markdown').toString(), this.seq);
    }
    presence(users) {
        const held = new Set(users.map(u => u.userId));
        const gone = [...this.awareness.getStates()].filter(([id, state]) => id !== this.doc.clientID && !held.has(state.user?.userId)).map(([id]) => id);
        removeAwarenessStates(this.awareness, gone, this.remote);
    }
    dispose() {
        if (this.disposed) return;
        this.disposed = true; clearTimeout(this.batchTimer); clearTimeout(this.copyTimer); clearTimeout(this.retry);clearTimeout(this.awarenessTimer);
        if (typeof window !== 'undefined') window.removeEventListener('beforeunload', this.beforeUnload);
        if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', this.visibility);
        this.awareness.destroy(); this.doc.destroy(); this.chunks.clear();
    }
}
