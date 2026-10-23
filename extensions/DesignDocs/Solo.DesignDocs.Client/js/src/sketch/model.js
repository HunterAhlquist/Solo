// A sketch's shared state in its Y.Doc:
//   ops    Y.Array of finished operations in drawing order. Each is a stroke
//          { id, tool: 'pen'|'eraser', ink, level, seed, pointer, points }.
//   texts  Y.Map of text boxes { x, y, ink, scale, text: Y.Text }.
// The page's size, orientation and pattern live in the sketch's row, which
// the REST API serves; the doc holds only what people draw.
//
// A move rewrites the moved strokes in one transaction rather than recording a
// move operation. It keeps erasing simple: an eraser cuts what was drawn before
// it, and the moved pieces are drawn after every eraser, already cut.

import * as Y from 'yjs';
import { ERASER_SCALE, brushMask, stamps, widthFor } from './brush.js';

export const INKS = Object.freeze(['theme', 'red', 'blue', 'green']);

export class SketchModel {
    constructor(doc) {
        this.doc = doc;
        this.ops = doc.getArray('ops');
        this.texts = doc.getMap('texts');
        this.origin = this;
        this.undoManager = new Y.UndoManager([this.ops, this.texts], { trackedOrigins: new Set([this]) });
    }

    strokes() {
        return this.ops.toArray();
    }

    addStroke(stroke) {
        this.doc.transact(() => this.ops.push([stroke]), this.origin);
    }

    /** A text box, its text editable by everyone at once. */
    addText(x, y, ink, scale = 1) {
        const id = newId();
        this.doc.transact(() => {
            const box = new Y.Map();
            box.set('x', x);
            box.set('y', y);
            box.set('ink', ink);
            box.set('scale', scale);
            box.set('text', new Y.Text());
            this.texts.set(id, box);
        }, this.origin);
        return id;
    }

    textBoxes() {
        const boxes = [];
        this.texts.forEach((box, id) => boxes.push({
            id, x: box.get('x'), y: box.get('y'), ink: box.get('ink'), scale: box.get('scale') ?? 1, text: box.get('text'),
        }));
        return boxes;
    }

    moveText(id, dx, dy) {
        const box = this.texts.get(id);
        if (!box) {
            return;
        }

        this.doc.transact(() => {
            box.set('x', Math.round(box.get('x') + dx));
            box.set('y', Math.round(box.get('y') + dy));
        }, this.origin);
    }

    removeText(id) {
        this.doc.transact(() => this.texts.delete(id), this.origin);
    }

    /** Sets a text box's text by the smallest change, so others typing in it keep their place. */
    setText(id, value) {
        const text = this.texts.get(id)?.get('text');
        if (!text) {
            return;
        }

        const current = text.toString();
        let start = 0;
        while (start < current.length && start < value.length && current[start] === value[start]) {
            start++;
        }

        let end = 0;
        while (end < current.length - start && end < value.length - start &&
               current[current.length - 1 - end] === value[value.length - 1 - end]) {
            end++;
        }

        this.doc.transact(() => {
            text.delete(start, current.length - start - end);
            text.insert(start, value.slice(start, value.length - end));
        }, this.origin);
    }

    clear() {
        this.doc.transact(() => {
            this.ops.delete(0, this.ops.length);
            for (const key of [...this.texts.keys()]) {
                this.texts.delete(key);
            }
        }, this.origin);
    }

    /**
     * Moves strokes by (dx, dy). Each moved stroke keeps only the points no
     * later eraser covers, split into its visible pieces; the pieces outside
     * `keep` stay where they were, the rest move and are drawn after every
     * eraser, so they look exactly as they did.
     *
     * @param {Set<string>} ids  Strokes to move.
     * @param {(x: number, y: number) => boolean} [inside]  Which visible points belong to the moved group.
     */
    moveStrokes(ids, dx, dy, inside = () => true) {
        const all = this.strokes();
        const moved = [];
        const stays = [];
        for (let index = 0; index < all.length; index++) {
            const stroke = all[index];
            if (!ids.has(stroke.id) || stroke.tool !== 'pen') {
                continue;
            }

            const erased = erasedPoints(stroke, all.slice(index + 1));
            const pieces = visiblePieces(stroke, erased);
            for (const piece of pieces) {
                const goes = piece.some((_, i) => i % 3 === 0 && inside(piece[i], piece[i + 1]));
                (goes ? moved : stays).push({ index, piece, stroke });
            }
        }

        if (moved.length === 0) {
            return;
        }

        this.doc.transact(() => {
            // Replace from the end, so earlier indexes stay valid.
            const touched = [...new Set([...moved, ...stays].map(m => m.index))].sort((a, b) => b - a);
            for (const index of touched) {
                const remaining = stays.filter(s => s.index === index)
                    .map(s => ({ ...s.stroke, id: newId(), points: s.piece }));
                this.ops.delete(index, 1);
                if (remaining.length > 0) {
                    this.ops.insert(index, remaining);
                }
            }

            this.ops.push(moved.map(m => ({
                ...m.stroke,
                id: newId(),
                points: m.piece.map((v, i) => (i % 3 === 0 ? round1(v + dx) : i % 3 === 1 ? round1(v + dy) : v)),
            })));
        }, this.origin);
    }

    /** The drawn area's bounds, or null when nothing visible is drawn. Erasers don't count. */
    contentBounds() {
        let [minX, minY, maxX, maxY] = [Infinity, Infinity, -Infinity, -Infinity];
        for (const stroke of this.strokes()) {
            if (stroke.tool !== 'pen') {
                continue;
            }

            for (let i = 0; i < stroke.points.length; i += 3) {
                const half = widthFor(stroke.level, stroke.points[i + 2], stroke.pointer) / 2;
                minX = Math.min(minX, stroke.points[i] - half);
                minY = Math.min(minY, stroke.points[i + 1] - half);
                maxX = Math.max(maxX, stroke.points[i] + half);
                maxY = Math.max(maxY, stroke.points[i + 1] + half);
            }
        }

        for (const box of this.textBoxes()) {
            const length = box.text.length;
            minX = Math.min(minX, box.x);
            minY = Math.min(minY, box.y);
            maxX = Math.max(maxX, box.x + length * 8 * box.scale);
            maxY = Math.max(maxY, box.y + 10 * box.scale);
        }

        return Number.isFinite(minX) ? { minX, minY, maxX, maxY } : null;
    }

    /** Whether a page of this size would hide any of the drawing. */
    wouldClip(width, height) {
        const bounds = this.contentBounds();
        return !!bounds && (bounds.minX < 0 || bounds.minY < 0 || bounds.maxX > width || bounds.maxY > height);
    }

    destroy() {
        this.undoManager.destroy();
    }
}

/** Indexes of a stroke's points that a later eraser stamp covers. */
export function erasedPoints(stroke, later) {
    const erased = new Set();
    const erasers = later.filter(op => op.tool === 'eraser');
    if (erasers.length === 0) {
        return erased;
    }

    const covered = new Set();
    for (const eraser of erasers) {
        for (const stamp of stamps(eraser.points, eraser.level, eraser.pointer, ERASER_SCALE)) {
            for (const [mx, my] of brushMask(stamp.width, stamp.angle)) {
                covered.add(key(stamp.x + mx, stamp.y + my));
            }
        }
    }

    for (let i = 0; i < stroke.points.length; i += 3) {
        if (covered.has(key(Math.round(stroke.points[i]), Math.round(stroke.points[i + 1])))) {
            erased.add(i / 3);
        }
    }

    return erased;
}

/** A stroke's runs of points between erased ones, each a flat points array. */
export function visiblePieces(stroke, erased) {
    const pieces = [];
    let current = [];
    for (let i = 0; i < stroke.points.length / 3; i++) {
        if (erased.has(i)) {
            if (current.length > 0) {
                pieces.push(current);
            }

            current = [];
        } else {
            current.push(stroke.points[i * 3], stroke.points[i * 3 + 1], stroke.points[i * 3 + 2]);
        }
    }

    if (current.length > 0) {
        pieces.push(current);
    }

    return pieces;
}

export function newId() {
    const bytes = new Uint8Array(8);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
}

export function newSeed() {
    return crypto.getRandomValues(new Uint32Array(1))[0];
}

const key = (x, y) => `${x},${y}`;
const round1 = v => Math.round(v * 10) / 10;
