// Keeps one Y.Doc in step with everyone else's through the server's relay
// (Solo.DesignDocs/Live/DocumentSyncHandler.cs), over core's hub.
//
// The provider never talks to the network itself: `send(kind, data)` goes out
// through .NET (ISoloLiveTopics.SendAsync) and resolves to the hub's
// LiveSendResult name, and whatever arrives on the document's topic is handed
// to `receive(kind, data)`. That keeps sign-in, reconnects and the desktop
// proxy core's business, and makes the provider testable with a fake relay.

import * as Y from 'yjs';
import * as awarenessProtocol from 'y-protocols/awareness';
import { Kinds, Reasons, PartAssembler, frame, read, write } from './protocol.js';
import { fromBase64, toBase64 } from './base64.js';

const ACCEPTED = 'Accepted';

export class SyncProvider {
    /**
     * @param {Y.Doc} doc
     * @param {object} options
     * @param {(kind: string, data: string|null) => Promise<string>} options.send
     * @param {boolean} options.canWrite  A writer: sends updates, awareness and compactions.
     * @param {awarenessProtocol.Awareness} [options.awareness]
     * @param {(scratch: Y.Doc, markdown: string) => void} [options.seed]  Builds a seed from imported text.
     */
    constructor(doc, options) {
        this.doc = doc;
        this.send = options.send;
        this.canWrite = !!options.canWrite;
        this.awareness = options.awareness ?? null;
        this.seedWith = options.seed ?? null;
        this.batchMs = options.batchMs ?? 50;
        this.awarenessMs = options.awarenessMs ?? 60;
        this.compactAfter = options.compactAfter ?? 500;
        this.retryMs = options.retryMs ?? 1000;
        this.timers = options.timers ?? { set: (f, ms) => setTimeout(f, ms), clear: id => clearTimeout(id) };
        this.on = { status: () => {}, reset: () => {}, unseeded: () => {}, ...options.on };

        this.lastSeq = 0;
        this.synced = false;
        this.connected = options.connected ?? true;
        this.destroyed = false;
        this.counter = 0;
        this.pending = new Map();
        this.buffer = [];
        this.flushTimer = null;
        this.retryTimer = null;
        this.retryDelay = this.retryMs;
        this.seedInFlight = null;
        this.sinceSnapshot = 0;
        this.awarenessTimer = null;
        this.parts = new PartAssembler();

        this.onDocUpdate = (update, origin) => {
            if (origin === this || !this.canWrite) {
                return;
            }

            this.buffer.push(update);
            if (this.flushTimer === null) {
                this.flushTimer = this.timers.set(() => this.flush(), this.batchMs);
            }
        };
        doc.on('update', this.onDocUpdate);

        if (this.awareness) {
            this.onAwarenessUpdate = ({ added, updated, removed }, origin) => {
                if (origin === this) {
                    // Someone new: tell them who we are now, rather than at our next renewal.
                    if (added.length > 0) {
                        this.scheduleAwareness();
                    }

                    return;
                }

                const local = this.awareness.clientID;
                if (added.includes(local) || updated.includes(local) || removed.includes(local)) {
                    this.scheduleAwareness();
                }
            };
            this.awareness.on('update', this.onAwarenessUpdate);
        }
    }

    /** Asks for everything after what this doc already has. */
    start() {
        this.synced = false;
        // A seed still out when we resync is answered by the sync itself: its
        // state holds the seed if it landed, or a fresh lease if it didn't.
        this.seedInFlight = null;
        this.emitStatus();
        this.sendFramed(Kinds.sync, write.sync(this.lastSeq));
    }

    /** The live connection went down or came back; core has resubscribed the topic by then. */
    setConnected(connected) {
        this.connected = connected;
        if (connected) {
            this.start();
        } else {
            this.synced = false;
            this.emitStatus();
        }
    }

    setCanWrite(canWrite) {
        this.canWrite = canWrite;
        this.emitStatus();
    }

    /** Peers core's presence says have left: their cursors go now, not at y-protocols' timeout. */
    removePeers(userIds) {
        if (!this.awareness) {
            return;
        }

        const gone = new Set(userIds);
        const clients = [];
        this.awareness.getStates().forEach((state, clientId) => {
            if (clientId !== this.awareness.clientID && gone.has(state?.user?.id)) {
                clients.push(clientId);
            }
        });
        if (clients.length > 0) {
            awarenessProtocol.removeAwarenessStates(this.awareness, clients, this);
        }
    }

    get unacked() {
        return this.pending.size + (this.seedInFlight ? 1 : 0) + this.buffer.length;
    }

    receive(kind, data) {
        if (this.destroyed) {
            return;
        }

        if (kind === Kinds.part) {
            const whole = this.parts.add(data);
            if (whole) {
                this.receive(whole.kind, whole.data);
            }

            return;
        }

        const message = kind === Kinds.awareness || kind === Kinds.seeding ? null : read(data);
        switch (kind) {
            case Kinds.state:
                Y.transact(this.doc, () => {
                    for (const [seq, entry] of message?.entries ?? []) {
                        Y.applyUpdate(this.doc, fromBase64(entry), this);
                        this.lastSeq = Math.max(this.lastSeq, seq);
                    }
                }, this);
                break;

            case Kinds.synced:
                this.lastSeq = Math.max(this.lastSeq, message?.seq ?? 0);
                this.sinceSnapshot = message?.pending ?? 0;
                this.synced = true;
                this.retryDelay = this.retryMs;
                // Whatever went out before a gap may never have landed.
                this.resendAll();
                this.scheduleAwareness();
                this.maybeCompact();
                this.emitStatus();
                break;

            case Kinds.update:
                if (message?.data) {
                    Y.applyUpdate(this.doc, fromBase64(message.data), this);
                    this.advance(message.seq);
                    this.sinceSnapshot++;
                }

                break;

            case Kinds.ack:
                this.acked(message);
                break;

            case Kinds.refused:
                this.refused(message);
                break;

            case Kinds.awareness:
                if (this.awareness && data) {
                    awarenessProtocol.applyAwarenessUpdate(this.awareness, fromBase64(data), this);
                }

                break;

            case Kinds.seed:
                this.seed(message);
                break;

            case Kinds.seeding:
                // Another writer is converting the imported text; ask again shortly.
                this.timers.set(() => !this.destroyed && this.start(), 1500);
                break;

            case Kinds.unseeded:
                this.on.unseeded(message?.markdown ?? '');
                break;
        }
    }

    /**
     * Moves lastSeq up for a relayed update or an ack, but only once synced.
     * Seqs aren't contiguous per document, so a gap can't be seen; before the
     * sync's answer, an update relayed after a reconnect could be newer than
     * ones this doc missed while offline, and resyncing from it would skip
     * them. Once synced, the hub delivers in order, so there are no gaps.
     */
    advance(seq) {
        if (this.synced) {
            this.lastSeq = Math.max(this.lastSeq, seq ?? 0);
        }
    }

    flush() {
        this.flushTimer = null;
        if (this.buffer.length === 0 || this.destroyed) {
            return;
        }

        const update = this.buffer.length === 1 ? this.buffer[0] : Y.mergeUpdates(this.buffer);
        this.buffer = [];
        const id = this.nextId();
        this.pending.set(id, update);
        this.emitStatus();
        this.sendUpdate(id, update);
    }

    async sendUpdate(id, update, lease) {
        if (!this.connected) {
            return;
        }

        const ok = await this.sendFramed(Kinds.update, write.update(id, toBase64(update), lease));
        if (!ok) {
            this.scheduleRetry();
        }
    }

    resendAll() {
        for (const [id, update] of this.pending) {
            this.sendUpdate(id, update);
        }
    }

    scheduleRetry() {
        if (this.retryTimer !== null || this.destroyed || !this.connected) {
            return;
        }

        this.retryTimer = this.timers.set(() => {
            this.retryTimer = null;
            this.resendAll();
        }, this.retryDelay);
        this.retryDelay = Math.min(this.retryDelay * 2, 10000);
    }

    acked(message) {
        if (!message) {
            return;
        }

        this.sinceSnapshot++;
        if (this.seedInFlight && this.seedInFlight.id === message.id) {
            // Only now does the seed become ours: had it lost, someone else's
            // copy of the same text would be arriving instead. It is the log's
            // first entry, so this doc is in sync up to it.
            Y.applyUpdate(this.doc, this.seedInFlight.update, this);
            this.seedInFlight = null;
            this.synced = true;
        } else {
            this.pending.delete(message.id);
        }

        this.advance(message.seq);

        this.maybeCompact();
        this.emitStatus();
    }

    refused(message) {
        if (!message) {
            return;
        }

        if (this.seedInFlight && this.seedInFlight.id === message.id) {
            this.seedInFlight = null;
            this.start();
            return;
        }

        this.pending.delete(message.id);
        switch (message.reason) {
            case Reasons.readOnly:
            case Reasons.closed:
                // This doc now holds text the server won't take. The editor
                // starts over from the server's copy, read-only.
                this.canWrite = false;
                this.pending.clear();
                this.buffer = [];
                this.on.reset(message.reason);
                break;
            case Reasons.unseeded:
            case Reasons.seedLost:
                this.on.reset(message.reason);
                break;
        }

        this.emitStatus();
    }

    seed(message) {
        if (!message || !this.seedWith || !this.canWrite) {
            return;
        }

        const scratch = new Y.Doc();
        this.seedWith(scratch, message.markdown ?? '');
        const update = Y.encodeStateAsUpdate(scratch);
        scratch.destroy();
        const id = this.nextId();
        this.seedInFlight = { id, update };
        this.emitStatus();
        this.sendFramed(Kinds.update, write.update(id, toBase64(update), message.lease)).then(ok => {
            // Not sent: drop it and ask again. The lease is ours for a while
            // yet, and a fresh sync renews it.
            if (!ok && this.seedInFlight?.id === id) {
                this.seedInFlight = null;
                this.timers.set(() => !this.destroyed && this.connected && this.start(), 1500);
            }
        });
    }

    maybeCompact() {
        if (!this.canWrite || !this.synced || this.lastSeq <= 0 || this.unacked > 0 ||
            this.sinceSnapshot < this.compactAfter) {
            return;
        }

        // Only with nothing unacked: a snapshot holding an update the server
        // later refused would keep it for good.
        this.sinceSnapshot = 0;
        this.sendFramed(Kinds.compact, write.compact(this.lastSeq, toBase64(Y.encodeStateAsUpdate(this.doc))));
    }

    scheduleAwareness() {
        if (!this.awareness || !this.canWrite || this.awarenessTimer !== null || this.destroyed) {
            return;
        }

        this.awarenessTimer = this.timers.set(() => {
            this.awarenessTimer = null;
            if (!this.connected || this.destroyed || !this.canWrite) {
                return;
            }

            const update = awarenessProtocol.encodeAwarenessUpdate(this.awareness, [this.awareness.clientID]);
            this.sendFramed(Kinds.awareness, toBase64(update));
        }, this.awarenessMs);
    }

    /** Sends one logical message, in parts if it's long. True when every part was accepted. */
    async sendFramed(kind, data) {
        if (!this.connected) {
            return false;
        }

        for (const message of frame(kind, data)) {
            let result;
            try {
                result = await this.send(message.kind, message.data);
            } catch {
                result = 'Failed';
            }

            if (result !== ACCEPTED) {
                return false;
            }
        }

        return true;
    }

    nextId() {
        return `${this.doc.clientID.toString(36)}-${(this.counter++).toString(36)}`;
    }

    emitStatus() {
        this.on.status({
            synced: this.synced,
            connected: this.connected,
            canWrite: this.canWrite,
            unacked: this.unacked,
            seeding: !!this.seedInFlight,
        });
    }

    destroy() {
        this.destroyed = true;
        this.doc.off('update', this.onDocUpdate);
        if (this.awareness) {
            this.awareness.off('update', this.onAwarenessUpdate);
            awarenessProtocol.removeAwarenessStates(this.awareness, [this.awareness.clientID], 'destroy');
        }

        for (const timer of [this.flushTimer, this.retryTimer, this.awarenessTimer]) {
            if (timer !== null) {
                this.timers.clear(timer);
            }
        }
    }
}
