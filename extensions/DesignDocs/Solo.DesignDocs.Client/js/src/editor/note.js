// A note's editor: CodeMirror 6 on the note's shared Y.Text, with live
// preview, remote cursors in each person's color, and per-person undo.
// The view owns its element; Blazor never renders inside it.

import { Compartment, EditorState } from '@codemirror/state';
import { EditorView, drawSelection, dropCursor, keymap, placeholder } from '@codemirror/view';
import { defaultKeymap, indentWithTab } from '@codemirror/commands';
import { markdown, markdownLanguage } from '@codemirror/lang-markdown';
import { autocompletion } from '@codemirror/autocomplete';
import { ensureSyntaxTree, syntaxTree } from '@codemirror/language';
import * as Y from 'yjs';
import { yCollab, yUndoManagerKeymap } from 'y-codemirror.next';
import { livePreview, refreshPreview } from './livePreview.js';
import { headings } from './markdown.js';

export const TEXT = 'text';

/**
 * Builds a note's Y.Text from imported markdown: what a seed is. Line endings
 * become '\n', since the editor counts positions with '\n' as the only line
 * break; a note nobody opens still exports with the endings it came in with.
 */
export function seedNote(doc, markdownText) {
    doc.getText(TEXT).insert(0, markdownText.replace(/\r\n?/g, '\n'));
}

export class NoteEditor {
    /**
     * @param {HTMLElement} parent
     * @param {object} options
     * @param {Y.Doc} options.doc
     * @param {import('y-protocols/awareness').Awareness} options.awareness
     * @param {boolean} options.canWrite
     * @param {string} options.noteId
     * @param {() => import('../links/obsidian.js').VaultIndex} options.index
     * @param {object} options.host
     */
    constructor(parent, options) {
        this.options = options;
        this.readOnly = new Compartment();
        this.tasks = new Map();
        this.wantedTasks = new Set();
        this.taskTimer = null;
        this.writable = options.canWrite;
        this.offline = false;
        this.frozenText = null;

        const ytext = options.doc.getText(TEXT);
        this.undoManager = new Y.UndoManager(ytext);
        const preview = livePreview({
            index: options.index,
            noteId: options.noteId,
            task: id => this.tasks.get(id),
            wantTask: id => this.wantTask(id),
            canWrite: () => this.editable,
            host: options.host,
        });

        this.view = new EditorView({
            parent,
            state: EditorState.create({
                doc: ytext.toString(),
                extensions: [
                    this.readOnly.of(this.readOnlyExtensions()),
                    // Positions have to match the Y.Text's one for one.
                    EditorState.lineSeparator.of('\n'),
                    keymap.of([...yUndoManagerKeymap, indentWithTab, ...defaultKeymap]),
                    drawSelection(),
                    dropCursor(),
                    EditorView.lineWrapping,
                    markdown({ base: markdownLanguage, addKeymap: true }),
                    autocompletion({ override: [context => this.completeWikiLink(context)], icons: false }),
                    yCollab(ytext, options.awareness, { undoManager: this.undoManager }),
                    preview,
                    placeholder(options.canWrite ? 'Start writing…' : ''),
                    EditorView.domEventHandlers({
                        paste: event => this.pasteImages(event, event.clipboardData?.files),
                        drop: event => this.pasteImages(event, event.dataTransfer?.files),
                        focus: () => options.host.focusChanged(true),
                        blur: () => options.host.focusChanged(false),
                    }),
                    EditorView.contentAttributes.of({ 'aria-label': 'Note text', spellcheck: 'true' }),
                ],
            }),
        });
    }

    get editable() {
        return this.writable && !this.offline;
    }

    readOnlyExtensions() {
        const locked = !this.editable;
        return [EditorState.readOnly.of(locked), EditorView.editable.of(!locked)];
    }

    /** Write access changed, or the connection dropped or came back. */
    setAccess(canWrite, online) {
        this.writable = canWrite;
        this.offline = !online;
        this.view.dispatch({ effects: [this.readOnly.reconfigure(this.readOnlyExtensions()), refreshPreview.of(null)] });
    }

    /** The tree or task names changed; redraw embeds and links. */
    refresh() {
        this.view.dispatch({ effects: refreshPreview.of(null) });
    }

    /** Text at the cursor, replacing the selection, as typed. */
    insert(text) {
        if (!this.editable) {
            return false;
        }

        const { from, to } = this.view.state.selection.main;
        this.view.dispatch({
            changes: { from, to, insert: text },
            selection: { anchor: from + text.length },
            scrollIntoView: true,
            userEvent: 'input.paste',
        });
        this.view.focus();
        return true;
    }

    /** Scrolls to the heading with this slug. False when there's none. */
    scrollToHeading(slugText) {
        const state = this.view.state;
        const tree = ensureSyntaxTree(state, state.doc.length, 500) ?? syntaxTree(state);
        const heading = headings(state.doc.toString(), tree).find(h => h.slug === slugText);
        if (!heading) {
            return false;
        }

        this.view.dispatch({ effects: EditorView.scrollIntoView(heading.from, { y: 'start', yMargin: 24 }) });
        return true;
    }

    /** The headings as the copy sends them. */
    headings() {
        const state = this.view.state;
        const tree = ensureSyntaxTree(state, state.doc.length, 500) ?? syntaxTree(state);
        return headings(state.doc.toString(), tree).map(({ level, text, slug }) => ({ level, text, slug }));
    }

    scrollToLine(lineNumber) {
        const line = this.view.state.doc.line(Math.min(Math.max(1, lineNumber), this.view.state.doc.lines));
        this.view.dispatch({ effects: EditorView.scrollIntoView(line.from, { y: 'start', yMargin: 24 }) });
    }

    wantTask(id) {
        if (this.tasks.has(id) || this.wantedTasks.has(id)) {
            return;
        }

        this.wantedTasks.add(id);
        if (this.taskTimer === null) {
            this.taskTimer = setTimeout(() => this.loadTasks(), 30);
        }
    }

    async loadTasks() {
        this.taskTimer = null;
        const ids = [...this.wantedTasks].filter(id => !this.tasks.has(id));
        if (ids.length === 0) {
            return;
        }

        try {
            const found = await this.options.host.lookupTasks(ids);
            for (const task of found ?? []) {
                this.tasks.set(task.id.replace(/-/g, '').toLowerCase(), task);
            }
        } catch {
            // Chips keep their written text.
        }

        this.refresh();
    }

    /** A task drawer saved or moved a task: its chip should show the new name or serial. */
    forgetTask(id) {
        const key = id.replace(/-/g, '').toLowerCase();
        this.tasks.delete(key);
        this.wantedTasks.delete(key);
        this.wantTask(key);
    }

    pasteImages(event, files) {
        const images = [...(files ?? [])].filter(f => f.type.startsWith('image/'));
        if (images.length === 0 || !this.editable) {
            return false;
        }

        event.preventDefault();
        this.options.host.uploadImages(images);
        return true;
    }

    completeWikiLink(context) {
        const before = context.matchBefore(/\[\[[^\]|#\n]*$/);
        if (!before) {
            return null;
        }

        const index = this.options.index();
        const query = before.text.slice(2).toLowerCase();
        const options = index.notes
            .filter(n => n.id !== this.options.noteId && n.path.toLowerCase().includes(query))
            .slice(0, 50)
            .map(n => ({ label: n.path, detail: n.folder ? undefined : '', apply: `${n.path}]]` }));
        return { from: before.from + 2, options, validFor: /^[^\]|#\n]*$/ };
    }

    destroy() {
        clearTimeout(this.taskTimer);
        this.undoManager.destroy();
        this.view.destroy();
    }
}
