// The relay protocol on a document's topic, as the browser writes and reads it.
// The server's copy is Solo.DesignDocs/Live/SyncProtocol.cs; both are pinned by
// test/fixtures/protocol-examples.json, which the C# and JS tests read.

export const Kinds = Object.freeze({
    // Client to server.
    sync: 'sync',
    update: 'update',
    awareness: 'awareness',
    compact: 'compact',
    // Server to client; update and awareness are relayed under their own names.
    state: 'state',
    synced: 'synced',
    ack: 'ack',
    refused: 'refused',
    seed: 'seed',
    seeding: 'seeding',
    unseeded: 'unseeded',
    // Either direction: one slice of a message too long to send whole.
    part: 'part',
});

export const Reasons = Object.freeze({
    readOnly: 'read-only',
    closed: 'closed',
    unseeded: 'unseeded',
    seedLost: 'seed-lost',
    malformed: 'malformed',
});

/** Data per hub message, under core's 48,000-character cap with room for a part's envelope. */
export const CHUNK_LENGTH = 46000;

/** The most a reassembled message may hold, as on the server. */
export const MAX_MESSAGE_LENGTH = 16 * 1024 * 1024;

/**
 * The messages to send for one logical message: itself when it fits, or its
 * data in parts of at most CHUNK_LENGTH characters.
 */
export function frame(kind, data, makeId = randomId) {
    if (data == null || data.length <= CHUNK_LENGTH) {
        return [{ kind, data }];
    }

    const id = makeId();
    const n = Math.ceil(data.length / CHUNK_LENGTH);
    const frames = [];
    for (let i = 0; i < n; i++) {
        const d = data.slice(i * CHUNK_LENGTH, (i + 1) * CHUNK_LENGTH);
        frames.push({ kind: Kinds.part, data: JSON.stringify({ k: kind, id, i, n, d }) });
    }

    return frames;
}

/** Puts parts back together. A message is handed back once its last part is in. */
export class PartAssembler {
    constructor(expiryMs = 60000, now = () => Date.now()) {
        this.expiryMs = expiryMs;
        this.now = now;
        this.pending = new Map();
    }

    /** Returns { kind, data } once whole, or null. */
    add(raw) {
        let part;
        try {
            part = JSON.parse(raw);
        } catch {
            return null;
        }

        const maxParts = Math.ceil(MAX_MESSAGE_LENGTH / CHUNK_LENGTH);
        if (!part || typeof part.k !== 'string' || part.k === Kinds.part || typeof part.id !== 'string' ||
            !Number.isInteger(part.i) || !Number.isInteger(part.n) || part.n < 1 || part.n > maxParts ||
            part.i < 0 || part.i >= part.n || typeof part.d !== 'string') {
            return null;
        }

        const now = this.now();
        for (const [key, entry] of this.pending) {
            if (entry.expires <= now) {
                this.pending.delete(key);
            }
        }

        let entry = this.pending.get(part.id);
        if (!entry || entry.kind !== part.k || entry.slices.length !== part.n) {
            entry = { kind: part.k, slices: new Array(part.n).fill(null), received: 0, expires: now + this.expiryMs };
            this.pending.set(part.id, entry);
        }

        if (entry.slices[part.i] === null) {
            entry.slices[part.i] = part.d;
            entry.received++;
        }

        if (entry.received < part.n) {
            return null;
        }

        this.pending.delete(part.id);
        return { kind: entry.kind, data: entry.slices.join('') };
    }
}

export function randomId() {
    const bytes = new Uint8Array(8);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
}

// Message shapes, mirroring the C# records.
export const write = {
    sync: afterSeq => JSON.stringify({ afterSeq }),
    update: (id, data, lease) => JSON.stringify(lease ? { id, data, lease } : { id, data }),
    compact: (uptoSeq, data) => JSON.stringify({ uptoSeq, data }),
};

export function read(data) {
    if (data == null || data === '') {
        return null;
    }

    try {
        return JSON.parse(data);
    } catch {
        return null;
    }
}
