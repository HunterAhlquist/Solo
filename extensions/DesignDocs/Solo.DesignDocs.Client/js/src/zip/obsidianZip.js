// Obsidian vaults in and out as zip files, read and written in the browser
// with fflate. Import v1 takes .md notes and the images they embed; everything
// else is listed as skipped, with why, before anything is created.

import { strFromU8, strToU8, unzipSync, zipSync } from 'fflate';
import * as Y from 'yjs';
import { isImageReference, isSketchReference, parseWikiLink, relativeTo } from '../links/obsidian.js';
import { fromBase64 } from '../sync/base64.js';

export const MAX_IMAGE_BYTES = 10 * 1024 * 1024;
const STORABLE = /\.(png|jpe?g|gif|webp)$/i;
const EMBEDS = /!\[\[([^\[\]\n]+?)\]\]|!\[[^\]\n]*\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;

/**
 * What a zip holds, sorted into notes, the images notes embed, and the rest.
 * @param {Uint8Array} bytes
 */
export function readVaultZip(bytes) {
    const files = unzipSync(bytes);
    let paths = Object.keys(files).filter(p => !p.endsWith('/'));

    // A zip of the vault's folder has the folder as every path's first segment.
    const roots = new Set(paths.map(p => p.split('/')[0]));
    const prefix = roots.size === 1 && paths.every(p => p.includes('/')) ? `${[...roots][0]}/` : '';

    const notes = [];
    const candidates = [];
    const skipped = [];
    for (const raw of paths) {
        const path = raw.slice(prefix.length);
        const segments = path.split('/');
        if (segments.some(s => s.startsWith('.')) || segments[0] === '__MACOSX') {
            if (!path.startsWith('.obsidian/') && segments[0] !== '__MACOSX') {
                skipped.push({ path, reason: 'Hidden file' });
            }

            continue;
        }

        if (/\.excalidraw\.md$/i.test(path)) {
            skipped.push({ path, reason: 'Excalidraw drawings aren\'t imported' });
        } else if (/\.md$/i.test(path)) {
            // ignoreBOM keeps a byte order mark in the text, so the note exports as it came in.
            notes.push({ path, markdown: new TextDecoder('utf-8', { ignoreBOM: true }).decode(files[raw]) });
        } else if (/\.canvas$/i.test(path)) {
            skipped.push({ path, reason: 'Canvas files aren\'t imported' });
        } else if (isImageReference(path)) {
            candidates.push({ path, bytes: files[raw] });
        } else {
            skipped.push({ path, reason: 'Not a note or an image' });
        }
    }

    // Only images some note embeds, resolved by Obsidian's rules.
    const embedded = new Set();
    const byPath = new Map(candidates.map(c => [c.path.toLowerCase(), c]));
    const byName = new Map();
    for (const candidate of candidates) {
        const name = candidate.path.split('/').pop().toLowerCase();
        byName.set(name, byName.has(name) ? null : candidate);
    }

    for (const note of notes) {
        for (const match of note.markdown.matchAll(EMBEDS)) {
            let reference = match[1] ? parseWikiLink(match[1]).target : match[2];
            if (match[2]) {
                try {
                    reference = decodeURIComponent(reference);
                } catch {
                    // Left as written.
                }
            }

            const folder = note.path.split('/').slice(0, -1).join('/');
            const besideNote = relativeTo(folder, reference)?.toLowerCase();
            const wanted = reference.replace(/^\.?\//, '').toLowerCase();
            const found = (besideNote && byPath.get(besideNote)) ?? byPath.get(wanted) ?? byName.get(wanted.split('/').pop());
            if (found) {
                embedded.add(found);
            }
        }
    }

    const images = [];
    for (const candidate of candidates) {
        if (!embedded.has(candidate)) {
            skipped.push({ path: candidate.path, reason: 'No note embeds it' });
        } else if (/\.svg$/i.test(candidate.path)) {
            skipped.push({ path: candidate.path, reason: 'SVG images aren\'t supported' });
        } else if (!STORABLE.test(candidate.path)) {
            skipped.push({ path: candidate.path, reason: 'Only PNG, JPEG, GIF and WebP images are imported' });
        } else if (candidate.bytes.length > MAX_IMAGE_BYTES) {
            skipped.push({ path: candidate.path, reason: 'Over 10 MB' });
        } else {
            images.push(candidate);
        }
    }

    const folders = [...new Set(notes.flatMap(n => {
        const segments = n.path.split('/').slice(0, -1);
        return segments.map((_, i) => segments.slice(0, i + 1).join('/'));
    }))].sort();

    return { notes, images, folders, skipped };
}

/** Notes in calls of at most 100 notes or 5 MB, the server's limits per call. */
export function batches(notes, maxNotes = 100, maxBytes = 5 * 1024 * 1024) {
    const result = [];
    let current = [];
    let size = 0;
    for (const note of notes) {
        const length = note.markdown.length;
        if (current.length > 0 && (current.length >= maxNotes || size + length > maxBytes)) {
            result.push(current);
            current = [];
            size = 0;
        }

        current.push(note);
        size += length;
    }

    if (current.length > 0) {
        result.push(current);
    }

    return result;
}

/** A note's exact text from its stored log, or its seed if nobody has opened it since import. */
export function textFromLog(log, name = 'text') {
    if ((log.entries?.length ?? 0) === 0) {
        return log.seedMarkdown ?? '';
    }

    const doc = new Y.Doc();
    try {
        for (const entry of log.entries) {
            Y.applyUpdate(doc, fromBase64(entry.data));
        }

        return doc.getText(name).toString();
    } finally {
        doc.destroy();
    }
}

/** A sketch's doc rebuilt from its log, for rendering. */
export function docFromLog(log) {
    const doc = new Y.Doc();
    for (const entry of log.entries ?? []) {
        Y.applyUpdate(doc, fromBase64(entry.data));
    }

    return doc;
}

/**
 * The embeds of sketches rewritten to their PNGs, in an exported copy only:
 * Obsidian can show a PNG but can't open a .sketch.
 */
export function rewriteSketchEmbeds(markdown) {
    return markdown.replace(/!\[\[([^\[\]\n|#]+?\.sketch)((?:[#|][^\[\]\n]*)?)\]\]/gi,
        (_, target, rest) => `![[${target}.png${rest}]]`);
}

/** The zip's bytes from { path: Uint8Array | string }. */
export function writeZip(entries) {
    const files = {};
    for (const [path, content] of Object.entries(entries)) {
        files[path] = typeof content === 'string' ? strToU8(content) : content;
    }

    return zipSync(files, { level: 6 });
}

export { strFromU8, isSketchReference };
