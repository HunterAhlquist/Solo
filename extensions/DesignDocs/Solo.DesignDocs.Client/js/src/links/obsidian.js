// Resolving Obsidian's links and embeds against a vault's tree, by Obsidian's
// rules (https://obsidian.md/help/links): an exact vault-relative path first,
// then a file name found once anywhere in the vault. Imported notes keep their
// links exactly as Obsidian wrote them, so these rules are what make them work.

/** `[[Target#Heading|Alias]]`'s inside, split into its parts. */
export function parseWikiLink(inner) {
    let target = inner;
    let alias = null;
    const pipe = target.indexOf('|');
    if (pipe >= 0) {
        alias = target.slice(pipe + 1).trim() || null;
        target = target.slice(0, pipe);
    }

    let heading = null;
    const hash = target.indexOf('#');
    if (hash >= 0) {
        heading = target.slice(hash + 1).trim() || null;
        target = target.slice(0, hash);
    }

    return { target: target.trim(), heading, alias };
}

/** Paths and lookups for one vault's tree (a VaultTreeDto, camel-cased). */
export class VaultIndex {
    constructor(tree) {
        this.tree = tree;
        const folders = new Map((tree.folders ?? []).map(f => [f.id, f]));
        this.folderPath = id => {
            const parts = [];
            for (let at = id ? folders.get(id) : null; at; at = at.parentId ? folders.get(at.parentId) : null) {
                parts.unshift(at.name);
            }

            return parts.join('/');
        };

        this.notes = (tree.notes ?? []).map(n => {
            const folder = this.folderPath(n.folderId);
            return { ...n, folder, path: folder ? `${folder}/${n.name}` : n.name };
        });
        this.notesById = new Map(this.notes.map(n => [n.id, n]));
        this.images = tree.images ?? [];
        this.sketches = tree.sketches ?? [];
    }

    /** The note a `[[link]]` target names, seen from the note it's written in. */
    resolveNote(target, fromNoteId = null) {
        const wanted = stripExtension(normalize(target), '.md');
        if (!wanted) {
            return fromNoteId ? this.notesById.get(fromNoteId) ?? null : null;
        }

        const lower = wanted.toLowerCase();
        const exact = this.notes.find(n => n.path.toLowerCase() === lower);
        if (exact) {
            return exact;
        }

        // A path that is a suffix of one note's path ("Folder/Note") or a bare name.
        const matches = this.notes.filter(n => n.path.toLowerCase().endsWith('/' + lower) || n.name.toLowerCase() === lower);
        if (matches.length === 1) {
            return matches[0];
        }

        // Several share the name: one beside the linking note wins, as Obsidian prefers.
        const from = fromNoteId ? this.notesById.get(fromNoteId) : null;
        return (from && matches.find(n => n.folder === from.folder)) ?? null;
    }

    /**
     * The image an embed names: `![[path]]` or `![](path)`, with %20 decoded for the latter.
     * Obsidian can write either relative to the note ("../attachments/a.png"), so that is tried
     * before the vault-relative path and the bare name.
     */
    resolveImage(reference, markdownStyle = false, fromNoteId = null) {
        let wanted = reference;
        if (markdownStyle) {
            try {
                wanted = decodeURIComponent(reference);
            } catch {
                wanted = reference;
            }
        }

        const from = fromNoteId ? this.notesById.get(fromNoteId) : null;
        const relative = from ? relativeTo(from.folder, wanted)?.toLowerCase() : null;
        const besideNote = relative ? this.images.find(i => i.path.toLowerCase() === relative) : null;
        if (besideNote) {
            return besideNote;
        }

        wanted = normalize(wanted).toLowerCase();
        if (!wanted) {
            return null;
        }

        const exact = this.images.find(i => i.path.toLowerCase() === wanted);
        if (exact) {
            return exact;
        }

        const name = wanted.split('/').pop();
        const matches = this.images.filter(i => i.path.toLowerCase().split('/').pop() === name);
        return matches.length === 1 ? matches[0] : null;
    }

    /** The sketch `![[Name.sketch]]` embeds. Sketch names are unique per vault. */
    resolveSketch(reference) {
        const name = stripExtension(normalize(reference), '.sketch');
        return name === null ? null : this.sketches.find(s => s.name.toLowerCase() === name.toLowerCase()) ?? null;
    }

    /** A free image path under `attachments/` for a new upload, as Obsidian names pasted images. */
    freeImagePath(fileName) {
        const dot = fileName.lastIndexOf('.');
        const stem = (dot > 0 ? fileName.slice(0, dot) : fileName).replace(/[\\/:[\]#^|*"<>?]/g, '-').trim() || 'image';
        const extension = dot > 0 ? fileName.slice(dot) : '';
        const taken = new Set(this.images.map(i => i.path.toLowerCase()));
        for (let n = 1; ; n++) {
            const candidate = `attachments/${n === 1 ? stem : `${stem} ${n}`}${extension}`;
            if (!taken.has(candidate.toLowerCase())) {
                return candidate;
            }
        }
    }
}

export const isSketchReference = reference => /\.sketch$/i.test(reference.trim());

export const isImageReference = reference => /\.(png|jpe?g|gif|webp|bmp|svg|avif|heic)$/i.test(reference.trim());

/** `reference` read from inside `folder`, with `.` and `..` applied; null if it climbs out of the vault. */
export function relativeTo(folder, reference) {
    const parts = folder ? folder.split('/') : [];
    for (const segment of reference.replace(/\\/g, '/').split('/')) {
        if (segment === '..') {
            if (!parts.length) {
                return null;
            }

            parts.pop();
        } else if (segment && segment !== '.') {
            parts.push(segment);
        }
    }

    return parts.length ? parts.join('/') : null;
}

function normalize(path) {
    return path.replace(/\\/g, '/').split('/').filter(Boolean).join('/').trim();
}

function stripExtension(path, extension) {
    return path.toLowerCase().endsWith(extension) ? path.slice(0, -extension.length) : path;
}
