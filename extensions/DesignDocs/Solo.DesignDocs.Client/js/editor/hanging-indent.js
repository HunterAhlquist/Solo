import { StateEffect } from '@codemirror/state';
import { Decoration, ViewPlugin } from '@codemirror/view';
import { syntaxTree } from '@codemirror/language';

const leadingSpace = text => /^[ \t]*/.exec(text)[0].length;

// Where a line's text begins once its indentation and list marker are skipped.
// Returns line.from when there is nothing to hang wrapped rows from.
export function hangingStart(state, line) {
    let start = line.from + leadingSpace(line.text);
    const node = start < line.to ? syntaxTree(state).resolveInner(start, 1) : null;
    if (node?.name === 'ListMark' && node.from === start) start = node.to + leadingSpace(state.doc.sliceString(node.to, line.to));
    return start < line.to ? start : line.from;
}

const remeasured = StateEffect.define();

// Wrapped rows of an indented line or list item line up under its text, as in
// Obsidian. The indent is measured from the rendered line rather than counted
// in characters: the body font is proportional, and a list marker is drawn as
// a bullet until the caret reaches it.
export const hangingIndent = ViewPlugin.fromClass(class {
    constructor(view) {
        this.decorations = Decoration.none;
        this.signature = '';
        this.measure = { read: current => this.read(current), write: (measured, current) => this.write(measured, current) };
        view.requestMeasure(this.measure);
    }
    update(update) {
        if (update.docChanged) this.decorations = this.decorations.map(update.changes);
        const applied = update.transactions.some(tr => tr.effects.some(effect => effect.is(remeasured)));
        if (applied && this.pending?.doc === update.state.doc) this.decorations = this.pending.decorations;
        else {
            if (applied) this.signature = '';
            update.view.requestMeasure(this.measure);
        }
    }
    read(view) {
        const { state } = view, widths = [];
        for (let pos = view.viewport.from; pos <= view.viewport.to;) {
            const line = state.doc.lineAt(pos), start = hangingStart(state, line);
            pos = line.to + 1;
            if (start === line.from) continue;
            const first = view.coordsAtPos(line.from, 1), text = view.coordsAtPos(start, 1);
            // A pane too narrow for the indentation wraps inside it; leave that line alone.
            if (!first || !text || text.top >= first.bottom) continue;
            const width = Math.round(text.left - first.left);
            if (width > 0) widths.push([line.from, width]);
        }
        return { doc: state.doc, widths };
    }
    write({ doc, widths }, view) {
        const signature = widths.join(';');
        if (signature === this.signature) return;
        this.signature = signature;
        this.pending = { doc, decorations: Decoration.set(widths.map(([from, width]) =>
            Decoration.line({ class: 'design-docs-hanging', attributes: { style: `--design-docs-hang:${width}px` } }).range(from))) };
        // Updates can't be dispatched from inside a measure; a microtask still lands before the frame paints.
        queueMicrotask(() => { if (!this.destroyed) view.dispatch({ effects: remeasured.of(null) }); });
    }
    destroy() { this.destroyed = true; }
}, { decorations: plugin => plugin.decorations });
