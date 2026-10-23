// The Design Docs browser module: everything a vault page needs from
// JavaScript, behind one session object per page. .NET creates the session
// with a bridge (a DotNetObjectReference to DocsBridge) and calls the
// session's methods; the session calls back through the bridge for anything
// that needs the network, navigation or the host's UI.
//
// Loaded as an ES module from the package's content folder (same-origin, so
// script-src 'self' admits it on the web and through the desktop proxy). It
// uses no eval, no inline script and no workers.

import * as Y from 'yjs';
import { Awareness } from 'y-protocols/awareness';
import { SyncProvider } from './sync/provider.js';
import { NoteEditor, TEXT, seedNote } from './editor/note.js';
import { VaultIndex } from './links/obsidian.js';
import { ImageUrls } from './images/cache.js';
import { encodeForUpload } from './images/encode.js';
import { memberColor, memberHighlight } from './colors.js';
import { SketchModel } from './sketch/model.js';
import { SketchRenderer, loadPixelFont } from './sketch/render.js';
import { pageSize } from './sketch/page.js';
import { SketchPreview } from './sketch/view.js';
import { SketchEditor } from './sketch/editor.js';
import { batches, docFromLog, readVaultZip, rewriteSketchEmbeds, textFromLog, writeZip } from './zip/obsidianZip.js';

/** Adds the package's stylesheet once per page load. */
export function ensureStyles(href) {
    if (!document.querySelector('link[data-design-docs-styles]')) {
        const link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = href;
        link.dataset.designDocsStyles = '';
        document.head.append(link);
    }
}

/** Steps back to the page a link was followed from; false when this tab has nowhere to go back to. */
export function goBack() {
    if (history.length > 1) {
        history.back();
        return true;
    }

    return false;
}

/**
 * @param {object} bridge  DotNetObjectReference to DocsBridge.
 * @param {{userId: string, userName: string, colorSlot: number, canWrite: boolean,
 *          tree: object, fontUrl: string}} options
 */
export function createSession(bridge, options) {
    return new Session(bridge, options);
}

const COPY_DELAY_MS = 2000;

class Session {
    constructor(bridge, options) {
        this.bridge = bridge;
        this.user = { id: options.userId, name: options.userName };
        this.colorSlot = options.colorSlot ?? 0;
        this.canWrite = !!options.canWrite;
        this.connected = options.connected ?? true;
        this.index = new VaultIndex(options.tree);
        this.docs = new Map();
        this.images = new ImageUrls(async imageId => {
            const bytes = await bridge.invokeMethodAsync('FetchImage', imageId);
            return { bytes, contentType: sniff(bytes) };
        });
        this.note = null;
        this.sketchEditor = null;
        this.pendingImport = null;
        this.fontUrl = options.fontUrl;
        loadPixelFont(options.fontUrl).then(loaded => {
            if (loaded) {
                for (const entry of this.docs.values()) {
                    entry.renderer?.invalidateText();
                }
            }
        });

        this.onBeforeUnload = e => {
            if ([...this.docs.values()].some(d => d.provider.unacked > 0)) {
                e.preventDefault();
                e.returnValue = '';
            }
        };
        window.addEventListener('beforeunload', this.onBeforeUnload);

        // The person can switch schemes while a sketch is open; ink and cursors follow.
        this.schemeObserver = new MutationObserver(() => {
            const dark = isDark();
            for (const entry of this.docs.values()) {
                entry.renderer?.setDark(dark);
                this.setLocalUser(entry);
            }
        });
        this.schemeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-color-scheme'] });

        this.host = {
            imageUrl: image => this.images.get(image.id, image.version),
            navigate: url => this.navigate(url),
            openNote: (noteId, slug) => bridge.invokeMethodAsync('OpenNote', noteId, slug),
            openTask: taskId => bridge.invokeMethodAsync('OpenTask', taskId),
            unresolved: target => bridge.invokeMethodAsync('Unresolved', target),
            lookupTasks: ids => bridge.invokeMethodAsync('LookupTasks', ids),
            focusChanged: focused => bridge.invokeMethodAsync('FocusChanged', focused),
            uploadImages: files => this.uploadImages(files),
            canWrite: () => this.canWrite && this.connected,
            canOpenSketch: () => true,
            openSketch: sketchId => bridge.invokeMethodAsync('OpenSketch', sketchId),
            mountSketchPreview: (element, sketch) => this.mountSketchPreview(element, sketch),
        };
    }

    // -- Calls from .NET ---------------------------------------------------------

    /** A message on a document's topic, as core delivered it. */
    receive(docId, kind, data) {
        this.docs.get(docId)?.provider.receive(kind, data);
    }

    /** The hub connection went down or came back (topics already resubscribed). */
    connection(connected) {
        this.connected = connected;
        for (const entry of this.docs.values()) {
            entry.provider.setConnected(connected);
        }

        this.applyAccess();
    }

    /** Write access changed: a role change, or the vault closed or reopened. */
    setAccess(canWrite) {
        this.canWrite = canWrite;
        for (const entry of this.docs.values()) {
            entry.provider.setCanWrite(canWrite);
            this.setLocalUser(entry);
        }

        this.applyAccess();
    }

    applyAccess() {
        this.note?.editor?.setAccess(this.canWrite, this.connected);
        this.sketchEditor?.editor.setCanWrite(this.canWrite && this.connected);
    }

    setTree(tree) {
        this.index = new VaultIndex(tree);
        for (const entry of this.docs.values()) {
            if (entry.renderer) {
                const sketch = this.index.sketches.find(s => s.id === entry.id);
                if (sketch) {
                    entry.renderer.setPage({ ...pageSize(sketch.pageSize, sketch.orientation), pattern: sketch.pattern });
                }
            }
        }

        this.note?.editor?.refresh();
    }

    peersGone(docId, userIds) {
        this.docs.get(docId)?.provider.removePeers(userIds);
    }

    forgetImage(imageId) {
        this.images.forget(imageId);
        this.note?.editor?.refresh();
    }

    forgetTask(taskId) {
        this.note?.editor?.forgetTask(taskId);
    }

    /** Opens a note in the element, scrolled to a heading when one is named. */
    async mountNote(element, noteId, headingSlug) {
        this.unmountNote();
        const note = { id: noteId, element, editor: null, copyTimer: null, unseeded: null, slug: headingSlug };
        this.note = note;
        const entry = await this.openDoc(noteId, 'note');
        if (this.note !== note) {
            this.closeDoc(noteId);
            return false;
        }

        note.entry = entry;
        note.rebuild = () => this.buildNoteEditor(note);
        entry.rebuilds.add(note.rebuild);
        note.onUpdate = (_, origin) => {
            if (origin !== entry.provider) {
                this.scheduleCopy(note);
            } else if (note.unseeded !== null) {
                // A writer seeded it meanwhile: show the live document instead.
                note.unseeded = null;
                note.rebuild();
            }
        };
        entry.doc.on('update', note.onUpdate);
        entry.onUnseeded = markdown => {
            note.unseeded = markdown;
            note.rebuild();
        };
        this.buildNoteEditor(note);
        return true;
    }

    buildNoteEditor(note) {
        note.editor?.destroy();
        note.element.replaceChildren();
        const entry = note.entry;
        let doc = entry.doc;
        if (note.unseeded !== null) {
            // A reader opening a note no writer has seeded: the imported text, read-only, local only.
            doc = new Y.Doc();
            seedNote(doc, note.unseeded);
        }

        note.editor = new NoteEditor(note.element, {
            doc,
            awareness: entry.awareness,
            canWrite: note.unseeded === null && this.canWrite,
            noteId: note.id,
            index: () => this.index,
            host: this.host,
        });
        note.editor.setAccess(note.unseeded === null && this.canWrite, this.connected && entry.provider.synced);
        if (note.slug && entry.provider.synced) {
            this.scrollToHeading(note.slug);
        }
    }

    scrollToHeading(slug) {
        const note = this.note;
        if (!note?.editor) {
            return false;
        }

        note.slug = null;
        const found = note.editor.scrollToHeading(slug);
        if (!found) {
            this.bridge.invokeMethodAsync('HeadingNotFound', slug);
        }

        return found;
    }

    unmountNote() {
        const note = this.note;
        if (!note) {
            return;
        }

        this.note = null;
        clearTimeout(note.copyTimer);
        if (note.entry) {
            note.entry.doc.off('update', note.onUpdate);
            note.entry.rebuilds.delete(note.rebuild);
            note.entry.onUnseeded = null;
            this.flushCopy(note);
        }

        note.editor?.destroy();
        note.element.replaceChildren();
        if (note.entry) {
            this.closeDoc(note.id);
        }
    }

    /** Text at the cursor of the open note, as typed. */
    insert(text) {
        return this.note?.editor?.insert(text) ?? false;
    }

    // -- Documents ---------------------------------------------------------------

    async openDoc(docId, kind) {
        let entry = this.docs.get(docId);
        if (entry) {
            entry.refs++;
            await entry.ready;
            return entry;
        }

        entry = this.createEntry(docId, kind);
        entry.refs = 1;
        this.docs.set(docId, entry);
        entry.ready = this.bridge.invokeMethodAsync('OpenDocument', docId).then(() => {
            if (this.docs.get(docId) === entry) {
                entry.provider.start();
            }
        });
        await entry.ready;
        return entry;
    }

    createEntry(docId, kind) {
        const doc = new Y.Doc();
        const awareness = new Awareness(doc);
        awareness.setLocalState(null);
        const entry = { id: docId, kind, doc, awareness, refs: 0, rebuilds: new Set(), onUnseeded: null, lastStatus: '' };
        entry.provider = new SyncProvider(doc, {
            send: (k, d) => this.bridge.invokeMethodAsync('Send', docId, k, d),
            canWrite: this.canWrite,
            awareness,
            connected: this.connected,
            seed: kind === 'note' ? seedNote : null,
            on: {
                status: status => this.reportStatus(entry, status),
                reset: reason => this.resetDoc(entry, reason),
                unseeded: markdown => entry.onUnseeded?.(markdown),
            },
        });
        this.setLocalUser(entry);
        return entry;
    }

    setLocalUser(entry) {
        if (this.canWrite) {
            const dark = isDark();
            entry.awareness.setLocalStateField('user', {
                id: this.user.id,
                name: this.user.name,
                color: memberColor(this.colorSlot, dark),
                colorLight: memberHighlight(this.colorSlot, dark),
            });
        } else {
            // Readers have no cursor and don't show up in anyone's editor.
            entry.awareness.setLocalState(null);
        }
    }

    /** The server refused what this doc holds: start over from its copy. */
    resetDoc(entry, reason) {
        const fresh = this.createEntry(entry.id, entry.kind);
        entry.provider.destroy();
        entry.doc.destroy();
        Object.assign(entry, {
            doc: fresh.doc, awareness: fresh.awareness, provider: fresh.provider, model: null, renderer: null,
        });
        fresh.provider.on.status = status => this.reportStatus(entry, status);
        fresh.provider.on.reset = r => this.resetDoc(entry, r);
        fresh.provider.on.unseeded = markdown => entry.onUnseeded?.(markdown);
        if (this.note?.entry === entry && this.note.onUpdate) {
            entry.doc.on('update', this.note.onUpdate);
        }

        for (const rebuild of entry.rebuilds) {
            rebuild();
        }

        entry.provider.start();
        this.bridge.invokeMethodAsync('DocumentReset', entry.id, reason);
    }

    closeDoc(docId) {
        const entry = this.docs.get(docId);
        if (!entry || --entry.refs > 0) {
            return;
        }

        this.docs.delete(docId);
        entry.provider.destroy();
        entry.renderer?.destroy();
        entry.model?.destroy();
        entry.doc.destroy();
        this.bridge.invokeMethodAsync('CloseDocument', docId).catch(() => {});
    }

    reportStatus(entry, status) {
        const text = JSON.stringify(status);
        if (text !== entry.lastStatus) {
            entry.lastStatus = text;
            if (this.note?.entry === entry && this.note.editor) {
                this.note.editor.setAccess(this.note.unseeded === null && this.canWrite, status.connected && status.synced);
                if (status.synced && this.note.slug) {
                    this.scrollToHeading(this.note.slug);
                }
            }

            this.bridge.invokeMethodAsync('Status', entry.id, status).catch(() => {});
        }
    }

    // -- The markdown copy ---------------------------------------------------------

    scheduleCopy(note) {
        clearTimeout(note.copyTimer);
        note.copyTimer = setTimeout(() => this.flushCopy(note), COPY_DELAY_MS);
    }

    /**
     * Sends the note's markdown copy and headings, once this person's edits
     * have landed, so its seq covers them. Only after local edits, never
     * remote ones: whoever made an edit sends its copy.
     */
    async flushCopy(note, attempt = 0) {
        clearTimeout(note.copyTimer);
        note.copyTimer = null;
        const entry = note.entry;
        if (!entry || !this.canWrite || note.unseeded !== null) {
            return;
        }

        if (entry.provider.unacked > 0 && attempt < 20) {
            note.copyTimer = setTimeout(() => this.flushCopy(note, attempt + 1), 500);
            return;
        }

        const markdown = entry.doc.getText(TEXT).toString();
        const headings = note.editor?.headings() ?? [];
        try {
            await this.bridge.invokeMethodAsync('SaveCopy', note.id, markdown, headings, entry.provider.lastSeq);
        } catch {
            // The next edit sends it again.
        }
    }

    // -- Navigation ------------------------------------------------------------------

    navigate(url) {
        if (/^[a-z][a-z0-9+.-]*:/i.test(url)) {
            window.open(url, '_blank', 'noopener,noreferrer');
        } else {
            this.bridge.invokeMethodAsync('Navigate', url);
        }
    }

    // -- Images ------------------------------------------------------------------------

    /** Uploads files from an <input type=file> the page rendered. */
    attachImageInput(input) {
        input.addEventListener('change', () => {
            const files = [...(input.files ?? [])];
            input.value = '';
            this.uploadImages(files);
        });
    }

    async uploadImages(files) {
        const inserted = [];
        for (const file of files) {
            if (file.type === 'image/svg+xml' || /\.svg$/i.test(file.name)) {
                this.bridge.invokeMethodAsync('Notify', `${file.name}: SVG images aren't supported.`, 'warning');
                continue;
            }

            try {
                const encoded = await encodeForUpload(file);
                const stem = (file.name || 'Pasted image').replace(/\.[^.]+$/, '') || 'Pasted image';
                const path = this.index.freeImagePath(stem + encoded.extension);
                const result = await this.uploadWithRetry(path, encoded.bytes, encoded.width, encoded.height);
                if (result?.image) {
                    this.index.images.push(result.image);
                    inserted.push(`![[${result.image.path}]]`);
                } else {
                    this.bridge.invokeMethodAsync('Notify', `${file.name}: ${result?.error ?? 'upload failed'}`, 'error');
                }
            } catch (e) {
                this.bridge.invokeMethodAsync('Notify', `${file.name}: ${e.message ?? e}`, 'error');
            }
        }

        if (inserted.length > 0) {
            // Side by side, so several images land as one stack.
            this.insert(inserted.join(' '));
        }
    }

    async uploadWithRetry(path, bytes, width, height) {
        for (let attempt = 0; attempt < 6; attempt++) {
            const result = await this.bridge.invokeMethodAsync('UploadImage', path, bytes, width, height);
            if (!result?.retryAfterMs) {
                return result;
            }

            await new Promise(resolve => setTimeout(resolve, result.retryAfterMs));
        }

        return { error: 'the server is busy; try again in a minute' };
    }

    // -- Sketches ------------------------------------------------------------------------

    sketchParts(entry) {
        if (!entry.model) {
            const sketch = this.index.sketches.find(s => s.id === entry.id);
            const page = sketch ? pageSize(sketch.pageSize, sketch.orientation) : pageSize('A4', 'portrait');
            entry.model = new SketchModel(entry.doc);
            entry.renderer = new SketchRenderer(entry.model, { ...page, pattern: sketch?.pattern ?? 'empty' }, { dark: isDark() });
        }

        return entry;
    }

    mountSketchPreview(element, sketch) {
        let preview = null;
        let closed = false;
        let entry = null;
        const build = () => {
            preview?.destroy();
            preview = new SketchPreview(element, this.sketchParts(entry).renderer, entry.awareness);
        };

        this.openDoc(sketch.id, 'sketch').then(opened => {
            if (closed) {
                this.closeDoc(sketch.id);
                return;
            }

            entry = opened;
            entry.rebuilds.add(build);
            build();
        });

        return {
            destroy: () => {
                closed = true;
                preview?.destroy();
                if (entry) {
                    entry.rebuilds.delete(build);
                    this.closeDoc(sketch.id);
                }
            },
        };
    }

    /** Opens the full-screen editor on a sketch; the toolbar calls the sketch* methods. */
    async mountSketchEditor(element, sketchId, settings) {
        this.unmountSketchEditor();
        const holder = { id: sketchId, editor: null, entry: null };
        this.sketchEditor = holder;
        const entry = await this.openDoc(sketchId, 'sketch');
        if (this.sketchEditor !== holder) {
            this.closeDoc(sketchId);
            return;
        }

        holder.entry = entry;
        holder.build = () => {
            holder.editor?.destroy();
            const parts = this.sketchParts(entry);
            holder.editor = new SketchEditor(element, parts.model, parts.renderer, entry.awareness, {
                canWrite: this.canWrite && this.connected,
                onChange: state => this.bridge.invokeMethodAsync('SketchView', state).catch(() => {}),
            });
            holder.editor.setTool(settings?.tool ?? 'pen');
            holder.editor.setInk(settings?.ink ?? 'theme');
            holder.editor.setLevel(settings?.level ?? 2);
        };
        entry.rebuilds.add(holder.build);
        holder.build();
    }

    unmountSketchEditor() {
        const holder = this.sketchEditor;
        if (!holder) {
            return;
        }

        this.sketchEditor = null;
        holder.editor?.destroy();
        if (holder.entry) {
            holder.entry.rebuilds.delete(holder.build);
            this.closeDoc(holder.id);
        }
    }

    sketch(command, value) {
        const editor = this.sketchEditor?.editor;
        if (!editor) {
            return;
        }

        switch (command) {
            case 'tool': editor.setTool(value); break;
            case 'ink': editor.setInk(value); break;
            case 'level': editor.setLevel(value); break;
            case 'constrain': editor.setConstrain(value); break;
            case 'undo': editor.undo(); break;
            case 'redo': editor.redo(); break;
            case 'clear': editor.clear(); break;
            case 'rotate': editor.rotate(value); break;
            case 'zoom': editor.zoom(value); break;
            case 'fit': editor.fit(); break;
        }
    }

    /** Whether a page of this size would hide any of the sketch's drawing. */
    async sketchWouldClip(sketchId, size, orientation) {
        const entry = await this.openDoc(sketchId, 'sketch');
        try {
            const { width, height } = pageSize(size, orientation);
            return this.sketchParts(entry).model.wouldClip(width, height);
        } finally {
            this.closeDoc(sketchId);
        }
    }

    async exportSketchPng(sketchId, fileName) {
        const entry = await this.openDoc(sketchId, 'sketch');
        try {
            const blob = await this.sketchParts(entry).renderer.exportPng();
            download(blob, fileName);
        } finally {
            this.closeDoc(sketchId);
        }
    }

    // -- Obsidian import and export --------------------------------------------------

    /** Reads a zip the person chose, and says what importing it would do. */
    async readImport(input) {
        const file = input.files?.[0];
        input.value = '';
        if (!file) {
            return null;
        }

        const vault = readVaultZip(new Uint8Array(await file.arrayBuffer()));
        this.pendingImport = vault;
        return {
            fileName: file.name,
            notes: vault.notes.length,
            images: vault.images.length,
            folders: vault.folders.length,
            skipped: vault.skipped,
        };
    }

    /** Imports what readImport read: notes in batches, then images one at a time. */
    async runImport() {
        const vault = this.pendingImport;
        this.pendingImport = null;
        if (!vault) {
            return null;
        }

        const total = vault.notes.length + vault.images.length;
        let done = 0;
        const progress = phase => this.bridge.invokeMethodAsync('ImportProgress', done, total, phase).catch(() => {});
        const skipped = [];
        let notes = 0;
        let images = 0;
        let first = true;
        for (const batch of batches(vault.notes)) {
            const result = await this.bridge.invokeMethodAsync('ImportNotes', first ? vault.folders : [], batch);
            first = false;
            if (!result) {
                skipped.push(...batch.map(n => `${n.path}: not imported`));
            } else {
                notes += result.notesCreated;
                skipped.push(...result.skipped);
            }

            done += batch.length;
            progress('notes');
        }

        for (const image of vault.images) {
            const bitmap = await dimensions(image.bytes, image.path);
            const result = await this.uploadWithRetry(image.path, image.bytes, bitmap.width, bitmap.height);
            if (result?.image) {
                images++;
            } else {
                skipped.push(`${image.path}: ${result?.error ?? 'not uploaded'}`);
            }

            done++;
            progress('images');
        }

        return { notes, images, skipped };
    }

    /**
     * Builds the vault as an Obsidian zip and downloads it: each note's exact
     * text from its log, the images at their paths, and each sketch as a PNG.
     */
    async exportVault(fileName) {
        const tree = this.index;
        const entries = {};
        for (const folder of tree.tree.folders ?? []) {
            entries[`${tree.folderPath(folder.id)}/`] = new Uint8Array(0);
        }

        const sketchNames = new Map();
        for (const sketch of tree.sketches) {
            const log = await this.bridge.invokeMethodAsync('GetLog', sketch.id);
            const doc = docFromLog(log);
            const model = new SketchModel(doc);
            const renderer = new SketchRenderer(model, { ...pageSize(sketch.pageSize, sketch.orientation), pattern: sketch.pattern });
            const blob = await renderer.exportPng();
            renderer.destroy();
            model.destroy();
            doc.destroy();
            const owner = tree.notesById.get(sketch.noteId);
            const path = `${owner?.folder ? `${owner.folder}/` : ''}${sketch.name}.sketch.png`;
            entries[path] = new Uint8Array(await blob.arrayBuffer());
            sketchNames.set(sketch.name.toLowerCase(), path);
        }

        for (const note of tree.notes) {
            const log = await this.bridge.invokeMethodAsync('GetLog', note.id);
            const untouched = (log.entries?.length ?? 0) === 0 && log.seedMarkdown != null;
            const text = textFromLog(log, TEXT);
            entries[`${note.path}.md`] = untouched || sketchNames.size === 0 ? text : rewriteSketchEmbeds(text);
        }

        for (const image of tree.images) {
            entries[image.path] = await this.bridge.invokeMethodAsync('FetchImage', image.id);
        }

        download(new Blob([writeZip(entries)], { type: 'application/zip' }), fileName);
        return Object.keys(entries).length;
    }

    destroy() {
        this.unmountSketchEditor();
        this.unmountNote();
        for (const id of [...this.docs.keys()]) {
            const entry = this.docs.get(id);
            entry.refs = 1;
            this.closeDoc(id);
        }

        this.images.dispose();
        this.schemeObserver.disconnect();
        window.removeEventListener('beforeunload', this.onBeforeUnload);
    }
}

function download(blob, fileName) {
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
}

/** An image's type from its first bytes, as the server sniffs it. */
function sniff(bytes) {
    if (bytes[0] === 0x89 && bytes[1] === 0x50) return 'image/png';
    if (bytes[0] === 0xff && bytes[1] === 0xd8) return 'image/jpeg';
    if (bytes[0] === 0x47 && bytes[1] === 0x49) return 'image/gif';
    if (bytes[8] === 0x57 && bytes[9] === 0x45) return 'image/webp';
    return 'application/octet-stream';
}

async function dimensions(bytes, path) {
    try {
        const bitmap = await createImageBitmap(new Blob([bytes], { type: sniff(bytes) }));
        const size = { width: bitmap.width, height: bitmap.height };
        bitmap.close?.();
        return size;
    } catch {
        return { width: 0, height: 0, path };
    }
}

/** Whether the app shows its dark scheme: core marks it on the root element. */
function isDark() {
    const scheme = document.documentElement.dataset.colorScheme;
    if (scheme) {
        return scheme === 'dark';
    }

    return typeof matchMedia === 'function' && matchMedia('(prefers-color-scheme: dark)').matches;
}
