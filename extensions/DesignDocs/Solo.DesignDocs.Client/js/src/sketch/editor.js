// The full-screen sketch editor: the page under a view that pans, zooms in
// whole-pixel steps and rotates, pointer input from a pen, a finger or a
// mouse, and the tools. The toolbar is Blazor's; it calls the setters here.

import { stamps } from './brush.js';
import { drawLiveStroke } from './render.js';
import { newSeed } from './model.js';
import { animate, currentFrame } from './view.js';

export const TOOLS = Object.freeze(['pen', 'eraser', 'rectangle', 'ellipse', 'select', 'text']);

const MAX_SCALE = 32;
const TAP_SLOP = 6;
const ROTATE_SNAP = (8 * Math.PI) / 180;

export class SketchEditor {
    /**
     * @param {HTMLElement} element  Filled by the editor's canvas.
     * @param {import('./model.js').SketchModel} model
     * @param {import('./render.js').SketchRenderer} renderer
     * @param {import('y-protocols/awareness').Awareness} awareness
     * @param {{canWrite: boolean, onChange?: (state: object) => void}} options
     */
    constructor(element, model, renderer, awareness, options) {
        this.element = element;
        this.model = model;
        this.renderer = renderer;
        this.awareness = awareness;
        this.canWrite = options.canWrite;
        this.onChange = options.onChange ?? (() => {});

        this.tool = 'pen';
        this.ink = 'theme';
        this.level = 2;
        this.constrain = false;
        this.view = { scale: 1, rotation: 0, panX: 0, panY: 0 };
        this.penSeen = false;
        this.touches = new Map();
        this.gesture = null;
        this.active = null;
        this.selection = null;
        this.textEditor = null;
        this.visible = true;

        this.canvas = document.createElement('canvas');
        this.canvas.className = 'dd-sketch-editor__canvas';
        this.canvas.tabIndex = 0;
        this.canvas.setAttribute('aria-label', 'Sketch page');
        element.append(this.canvas);

        this.listen();
        this.stopChanges = renderer.onChange(() => this.draw());
        this.onAwareness = () => this.draw();
        awareness.on('change', this.onAwareness);
        this.stopAnimating = animate(this);
        this.resizeObserver = new ResizeObserver(() => this.resize());
        this.resizeObserver.observe(element);
        this.resize();
        this.fit();
    }

    // -- Settings from the toolbar ---------------------------------------------

    setTool(tool) {
        if (TOOLS.includes(tool)) {
            this.tool = tool;
            this.selection = null;
            this.closeTextEditor();
            this.draw();
        }
    }

    setInk(ink) {
        this.ink = ink;
    }

    setLevel(level) {
        this.level = Math.min(5, Math.max(1, level | 0));
    }

    setConstrain(on) {
        this.constrain = !!on;
    }

    setCanWrite(canWrite) {
        this.canWrite = canWrite;
        if (!canWrite) {
            this.cancelActive();
            this.closeTextEditor();
        }

        this.draw();
    }

    undo() {
        this.model.undoManager.undo();
    }

    redo() {
        this.model.undoManager.redo();
    }

    clear() {
        this.model.clear();
    }

    rotate(quarterTurns) {
        this.view.rotation = normalizeAngle(this.view.rotation + (quarterTurns * Math.PI) / 2);
        this.draw();
    }

    zoom(step) {
        this.setScale(this.view.scale + step, this.canvas.width / 2, this.canvas.height / 2);
    }

    /** The largest whole-pixel scale at which the page fits, centered. */
    fit() {
        const turned = Math.abs(Math.sin(this.view.rotation)) > 0.5;
        const [w, h] = turned ? [this.renderer.height, this.renderer.width] : [this.renderer.width, this.renderer.height];
        this.view.scale = Math.max(1, Math.floor(Math.min(this.canvas.width / w, this.canvas.height / h) * 0.95));
        this.view.panX = 0;
        this.view.panY = 0;
        this.draw();
        this.report();
    }

    report() {
        this.onChange({ scale: this.view.scale, rotation: Math.round((this.view.rotation * 180) / Math.PI) });
    }

    // -- View transform --------------------------------------------------------

    resize() {
        const ratio = window.devicePixelRatio || 1;
        const rect = this.element.getBoundingClientRect();
        this.canvas.width = Math.max(1, Math.round(rect.width * ratio));
        this.canvas.height = Math.max(1, Math.round(rect.height * ratio));
        this.canvas.style.width = `${rect.width}px`;
        this.canvas.style.height = `${rect.height}px`;
        this.draw();
    }

    /** Page to canvas pixels: center, pan, rotate, scale, page centered on the origin. */
    matrix() {
        const { scale, rotation, panX, panY } = this.view;
        const [cos, sin] = [Math.cos(rotation) * scale, Math.sin(rotation) * scale];
        const tx = this.canvas.width / 2 + panX;
        const ty = this.canvas.height / 2 + panY;
        const [cx, cy] = [this.renderer.width / 2, this.renderer.height / 2];
        return [cos, sin, -sin, cos, tx - cos * cx + sin * cy, ty - sin * cx - cos * cy];
    }

    toPage(clientX, clientY) {
        const rect = this.canvas.getBoundingClientRect();
        const ratio = this.canvas.width / rect.width;
        const x = (clientX - rect.left) * ratio;
        const y = (clientY - rect.top) * ratio;
        const [a, b, c, d, e, f] = this.matrix();
        const determinant = a * d - b * c;
        return {
            x: (d * (x - e) - c * (y - f)) / determinant,
            y: (-b * (x - e) + a * (y - f)) / determinant,
        };
    }

    setScale(scale, aboutX, aboutY) {
        const next = Math.min(MAX_SCALE, Math.max(1, Math.round(scale)));
        if (next === this.view.scale) {
            return;
        }

        // Keep the point under (aboutX, aboutY) where it is.
        const factor = next / this.view.scale;
        const cx = this.canvas.width / 2 + this.view.panX;
        const cy = this.canvas.height / 2 + this.view.panY;
        this.view.panX += (aboutX - cx) * (1 - factor);
        this.view.panY += (aboutY - cy) * (1 - factor);
        this.view.scale = next;
        this.draw();
        this.report();
    }

    // -- Drawing ---------------------------------------------------------------

    draw() {
        const context = this.canvas.getContext('2d');
        context.setTransform(1, 0, 0, 1, 0, 0);
        context.fillStyle = getComputedStyle(this.element).getPropertyValue('--dd-sketch-backdrop') || '#888';
        context.fillRect(0, 0, this.canvas.width, this.canvas.height);
        context.imageSmoothingEnabled = false;
        context.setTransform(...this.matrix());

        // The page, drawn once at paint size and scaled up as whole pixels.
        if (!this.pageCanvas || this.pageCanvas.width !== this.renderer.width || this.pageCanvas.height !== this.renderer.height) {
            this.pageCanvas = this.renderer.canvas();
        }

        const page = this.pageCanvas.getContext('2d');
        this.renderer.compose(page, currentFrame());
        this.drawLive(page);
        context.drawImage(this.pageCanvas, 0, 0);

        // Outline and pointers in screen space, so they stay thin and readable.
        context.setTransform(1, 0, 0, 1, 0, 0);
        this.drawPointers(context);
    }

    drawLive(page) {
        // Others' strokes in progress, then this person's.
        this.awareness.getStates().forEach((state, clientId) => {
            if (clientId !== this.awareness.clientID && state?.drawing) {
                drawLiveStroke(page, state.drawing, this.renderer.inkColor(state.drawing.ink), this.renderer.pageColor());
            }
        });

        if (this.active?.stroke) {
            drawLiveStroke(page, this.active.stroke, this.renderer.inkColor(this.active.stroke.ink), this.renderer.pageColor());
        }

        if (this.selection) {
            const { minX, minY, maxX, maxY } = this.selection.bounds;
            const [dx, dy] = this.selection.offset;
            page.strokeStyle = this.renderer.inkColor('blue');
            page.setLineDash([2, 2]);
            page.lineWidth = 1;
            page.strokeRect(minX + dx - 1.5, minY + dy - 1.5, maxX - minX + 3, maxY - minY + 3);
            page.setLineDash([]);
            if (dx !== 0 || dy !== 0) {
                page.globalAlpha = 0.6;
                page.drawImage(this.selection.pixels, minX + dx, minY + dy);
                page.globalAlpha = 1;
            }
        }
    }

    drawPointers(context) {
        const [a, b, c, d, e, f] = this.matrix();
        const ratio = window.devicePixelRatio || 1;
        context.font = `${11 * ratio}px system-ui, sans-serif`;
        this.awareness.getStates().forEach((state, clientId) => {
            if (clientId === this.awareness.clientID || !state?.pointer || !state.user) {
                return;
            }

            const { x, y } = state.pointer;
            const sx = a * x + c * y + e;
            const sy = b * x + d * y + f;
            context.fillStyle = state.user.color ?? '#888';
            context.strokeStyle = '#fff';
            context.lineWidth = 2 * ratio;
            context.beginPath();
            context.arc(sx, sy, 4 * ratio, 0, Math.PI * 2);
            context.stroke();
            context.fill();
            const label = state.user.name ?? '';
            if (label) {
                const width = context.measureText(label).width + 8 * ratio;
                context.fillRect(sx + 8 * ratio, sy + 6 * ratio, width, 16 * ratio);
                context.fillStyle = '#fff';
                context.fillText(label, sx + 12 * ratio, sy + 18 * ratio);
            }
        });
    }

    // -- Input -----------------------------------------------------------------

    listen() {
        const canvas = this.canvas;
        const handlers = {
            pointerdown: e => this.down(e),
            pointermove: e => this.move(e),
            pointerup: e => this.up(e, false),
            pointercancel: e => this.up(e, true),
            pointerleave: e => this.leave(e),
            wheel: e => this.wheel(e),
            contextmenu: e => e.preventDefault(),
            keydown: e => this.key(e),
        };
        for (const [name, handler] of Object.entries(handlers)) {
            canvas.addEventListener(name, handler, { passive: false });
        }

        // iOS: no callout, no magnifier, no double-tap zoom on the page.
        canvas.addEventListener('touchstart', e => e.preventDefault(), { passive: false });
        this.unlisten = () => {
            for (const [name, handler] of Object.entries(handlers)) {
                canvas.removeEventListener(name, handler);
            }
        };
    }

    down(e) {
        e.preventDefault();
        this.canvas.focus({ preventScroll: true });
        if (e.pointerType === 'pen') {
            this.penSeen = true;
            // A palm that landed first: its stroke goes.
            if (this.active?.pointerType === 'touch') {
                this.cancelActive();
            }
        }

        if (e.pointerType === 'touch') {
            this.touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
            // Palm rejection: while a pen is down, touches are ignored.
            if (this.active?.pointerType === 'pen') {
                return;
            }

            // Once a pen has been used, a finger moves the view rather than drawing.
            if (this.touches.size >= 2 || this.penSeen) {
                this.cancelActive();
                this.startGesture();
                return;
            }
        }

        if (e.pointerType === 'mouse' && (e.button === 1 || (e.button === 0 && e.altKey))) {
            this.active = { kind: 'pan', pointerId: e.pointerId, pointerType: 'mouse', last: { x: e.clientX, y: e.clientY } };
            this.canvas.setPointerCapture(e.pointerId);
            return;
        }

        if (e.pointerType === 'mouse' && e.button !== 0) {
            return;
        }

        this.canvas.setPointerCapture(e.pointerId);
        this.begin(e);
    }

    move(e) {
        if (e.pointerType === 'touch' && this.touches.has(e.pointerId)) {
            this.touches.set(e.pointerId, { x: e.clientX, y: e.clientY });
            if (this.gesture) {
                this.updateGesture();
                return;
            }
        }

        const point = this.toPage(e.clientX, e.clientY);
        if (this.canWrite && (e.pointerType !== 'touch' || !this.penSeen)) {
            this.awareness.setLocalStateField('pointer', { x: Math.round(point.x), y: Math.round(point.y) });
        }

        if (!this.active || this.active.pointerId !== e.pointerId) {
            return;
        }

        e.preventDefault();
        const events = typeof e.getCoalescedEvents === 'function' ? e.getCoalescedEvents() : [];
        this.extend(events.length > 0 ? events : [e]);
    }

    up(e, cancelled) {
        if (e.pointerType === 'touch') {
            this.touches.delete(e.pointerId);
            if (this.gesture) {
                if (this.touches.size < 2 && !this.penSeen) {
                    this.gesture = null;
                } else if (this.touches.size === 0) {
                    this.gesture = null;
                } else {
                    this.startGesture();
                }

                return;
            }
        }

        if (!this.active || this.active.pointerId !== e.pointerId) {
            return;
        }

        if (cancelled) {
            this.cancelActive();
        } else {
            this.finish(e);
        }
    }

    leave(e) {
        if (e.pointerType !== 'touch' && !this.active) {
            this.awareness.setLocalStateField('pointer', null);
        }
    }

    wheel(e) {
        e.preventDefault();
        const rect = this.canvas.getBoundingClientRect();
        const ratio = this.canvas.width / rect.width;
        if (e.ctrlKey || e.metaKey) {
            this.setScale(this.view.scale + (e.deltaY < 0 ? 1 : -1), (e.clientX - rect.left) * ratio, (e.clientY - rect.top) * ratio);
        } else {
            this.view.panX -= e.deltaX * ratio;
            this.view.panY -= e.deltaY * ratio;
            this.draw();
        }
    }

    key(e) {
        const mod = e.metaKey || e.ctrlKey;
        if (mod && e.key.toLowerCase() === 'z') {
            e.preventDefault();
            e.shiftKey ? this.redo() : this.undo();
        } else if (mod && e.key.toLowerCase() === 'y') {
            e.preventDefault();
            this.redo();
        } else if (e.key === 'Escape') {
            this.selection = null;
            this.draw();
        }
    }

    startGesture() {
        const [p1, p2] = [...this.touches.values()];
        if (!p2) {
            // One finger after a pen: it pans.
            this.gesture = { single: true, start: { ...p1 }, view: { ...this.view } };
            return;
        }

        this.gesture = {
            single: false,
            distance: Math.hypot(p2.x - p1.x, p2.y - p1.y) || 1,
            angle: Math.atan2(p2.y - p1.y, p2.x - p1.x),
            mid: { x: (p1.x + p2.x) / 2, y: (p1.y + p2.y) / 2 },
            view: { ...this.view },
        };
    }

    updateGesture() {
        const ratio = this.canvas.width / this.canvas.getBoundingClientRect().width;
        const [p1, p2] = [...this.touches.values()];
        const g = this.gesture;
        if (g.single || !p2) {
            const start = g.start ?? g.mid;
            this.view.panX = g.view.panX + (p1.x - start.x) * ratio;
            this.view.panY = g.view.panY + (p1.y - start.y) * ratio;
            this.draw();
            return;
        }

        const distance = Math.hypot(p2.x - p1.x, p2.y - p1.y) || 1;
        const angle = Math.atan2(p2.y - p1.y, p2.x - p1.x);
        const mid = { x: (p1.x + p2.x) / 2, y: (p1.y + p2.y) / 2 };
        let rotation = normalizeAngle(g.view.rotation + angle - g.angle);
        for (let quarter = 0; quarter < 4; quarter++) {
            const target = (quarter * Math.PI) / 2;
            if (Math.abs(normalizeAngle(rotation - target)) < ROTATE_SNAP) {
                rotation = target;
            }
        }

        this.view.rotation = rotation;
        this.view.scale = Math.min(MAX_SCALE, Math.max(1, Math.round(g.view.scale * (distance / g.distance))));
        this.view.panX = g.view.panX + (mid.x - g.mid.x) * ratio;
        this.view.panY = g.view.panY + (mid.y - g.mid.y) * ratio;
        this.draw();
        this.report();
    }

    // -- Tools -----------------------------------------------------------------

    begin(e) {
        const point = this.toPage(e.clientX, e.clientY);
        const base = { pointerId: e.pointerId, pointerType: e.pointerType, start: point, last: { x: e.clientX, y: e.clientY } };
        if (!this.canWrite) {
            this.active = { ...base, kind: 'pan', view: { ...this.view } };
            return;
        }

        switch (this.tool) {
            case 'pen':
            case 'eraser':
                this.active = {
                    ...base,
                    kind: 'stroke',
                    stroke: {
                        tool: this.tool,
                        ink: this.ink,
                        level: this.level,
                        seed: newSeed(),
                        pointer: e.pointerType,
                        points: [round1(point.x), round1(point.y), round2(e.pressure)],
                    },
                };
                break;
            case 'rectangle':
            case 'ellipse':
                this.active = { ...base, kind: 'shape', stroke: null };
                break;
            case 'select':
                if (this.selection && inside(this.selection.bounds, point, 3)) {
                    this.active = { ...base, kind: 'drag' };
                } else {
                    this.active = { ...base, kind: 'tap' };
                }

                break;
            case 'text':
                this.active = { ...base, kind: 'text' };
                break;
        }

        this.publishDrawing();
        this.draw();
    }

    extend(events) {
        const active = this.active;
        if (active.kind === 'pan') {
            const last = events[events.length - 1];
            const ratio = this.canvas.width / this.canvas.getBoundingClientRect().width;
            this.view.panX += (last.clientX - active.last.x) * ratio;
            this.view.panY += (last.clientY - active.last.y) * ratio;
            active.last = { x: last.clientX, y: last.clientY };
            this.draw();
            return;
        }

        const point = this.toPage(events[events.length - 1].clientX, events[events.length - 1].clientY);
        switch (active.kind) {
            case 'stroke':
                for (const event of events) {
                    const p = this.toPage(event.clientX, event.clientY);
                    const points = active.stroke.points;
                    const [lx, ly] = [points[points.length - 3], points[points.length - 2]];
                    if (Math.hypot(p.x - lx, p.y - ly) >= 0.5) {
                        points.push(round1(p.x), round1(p.y), round2(event.pressure));
                    }
                }

                break;
            case 'shape':
                active.stroke = shapeStroke(this.tool, active.start, point, this.constrain || events[events.length - 1].shiftKey,
                    this.ink, this.level, active.stroke?.seed ?? newSeed());
                break;
            case 'drag':
            case 'tap':
                if (this.selection && (active.kind === 'drag' || Math.hypot(point.x - active.start.x, point.y - active.start.y) * this.view.scale > TAP_SLOP)) {
                    active.kind = 'drag';
                    this.selection.offset = [Math.round(point.x - active.start.x), Math.round(point.y - active.start.y)];
                }

                break;
        }

        this.publishDrawing();
        this.draw();
    }

    finish(e) {
        const active = this.active;
        this.active = null;
        const point = this.toPage(e.clientX, e.clientY);
        switch (active.kind) {
            case 'stroke':
                this.model.addStroke({ id: crypto.randomUUID?.() ?? String(active.stroke.seed), ...active.stroke });
                break;
            case 'shape':
                if (active.stroke) {
                    this.model.addStroke({ id: crypto.randomUUID?.() ?? String(active.stroke.seed), ...active.stroke });
                }

                break;
            case 'tap':
                this.select(point);
                break;
            case 'drag':
                this.dropSelection();
                break;
            case 'text':
                this.editText(point);
                break;
        }

        this.publishDrawing();
        this.draw();
    }

    cancelActive() {
        this.active = null;
        this.publishDrawing();
        this.draw();
    }

    publishDrawing() {
        if (!this.canWrite) {
            return;
        }

        const stroke = this.active?.stroke;
        this.awareness.setLocalStateField('drawing', stroke ? { ...stroke, points: [...stroke.points] } : null);
    }

    /**
     * Tap to select: the ink touching the tapped pixel, by flood fill over
     * what's visible, and the strokes whose stamps land in it, or a text box.
     */
    select(point) {
        const box = this.model.textBoxes().find(t => hitText(t, point));
        if (box) {
            this.selection = { textId: box.id, bounds: textBounds(box), offset: [0, 0], pixels: this.crop(textBounds(box)) };
            return;
        }

        const layer = this.renderer.inkLayer(0);
        const { width, height } = layer;
        const alpha = layer.getContext('2d').getImageData(0, 0, width, height).data;
        const has = (x, y) => x >= 0 && y >= 0 && x < width && y < height && alpha[(y * width + x) * 4 + 3] > 0;
        let seed = null;
        for (let r = 0; r <= 4 && !seed; r++) {
            for (let dy = -r; dy <= r && !seed; dy++) {
                for (let dx = -r; dx <= r && !seed; dx++) {
                    if (has(Math.round(point.x) + dx, Math.round(point.y) + dy)) {
                        seed = [Math.round(point.x) + dx, Math.round(point.y) + dy];
                    }
                }
            }
        }

        if (!seed) {
            this.selection = null;
            return;
        }

        const region = new Set();
        const stack = [seed];
        let [minX, minY, maxX, maxY] = [seed[0], seed[1], seed[0], seed[1]];
        while (stack.length > 0) {
            const [x, y] = stack.pop();
            const key = y * width + x;
            if (region.has(key) || !has(x, y)) {
                continue;
            }

            region.add(key);
            [minX, minY, maxX, maxY] = [Math.min(minX, x), Math.min(minY, y), Math.max(maxX, x), Math.max(maxY, y)];
            for (let dy = -1; dy <= 1; dy++) {
                for (let dx = -1; dx <= 1; dx++) {
                    if (dx || dy) {
                        stack.push([x + dx, y + dy]);
                    }
                }
            }
        }

        const inRegion = (x, y) => {
            const [rx, ry] = [Math.round(x), Math.round(y)];
            for (let dy = -1; dy <= 1; dy++) {
                for (let dx = -1; dx <= 1; dx++) {
                    if (region.has((ry + dy) * width + rx + dx)) {
                        return true;
                    }
                }
            }

            return false;
        };
        const ids = new Set(this.model.strokes()
            .filter(s => s.tool === 'pen' && stamps(s.points, s.level, s.pointer).some(st => inRegion(st.x, st.y)))
            .map(s => s.id));
        const bounds = { minX, minY, maxX, maxY };
        this.selection = { ids, inRegion, bounds, offset: [0, 0], pixels: this.crop(bounds, region, width) };
    }

    crop(bounds, region = null, width = 0) {
        const canvas = document.createElement('canvas');
        canvas.width = bounds.maxX - bounds.minX + 1;
        canvas.height = bounds.maxY - bounds.minY + 1;
        const context = canvas.getContext('2d');
        context.drawImage(this.renderer.inkLayer(0), -bounds.minX, -bounds.minY);
        context.drawImage(this.renderer.textCanvas(), -bounds.minX, -bounds.minY);
        if (region) {
            const image = context.getImageData(0, 0, canvas.width, canvas.height);
            for (let y = 0; y < canvas.height; y++) {
                for (let x = 0; x < canvas.width; x++) {
                    if (!region.has((y + bounds.minY) * width + x + bounds.minX)) {
                        image.data[(y * canvas.width + x) * 4 + 3] = 0;
                    }
                }
            }

            context.putImageData(image, 0, 0);
        }

        return canvas;
    }

    dropSelection() {
        const selection = this.selection;
        if (!selection) {
            return;
        }

        const [dx, dy] = selection.offset;
        if (dx !== 0 || dy !== 0) {
            if (selection.textId) {
                this.model.moveText(selection.textId, dx, dy);
            } else {
                this.model.moveStrokes(selection.ids, dx, dy, selection.inRegion);
            }
        }

        this.selection = null;
    }

    editText(point) {
        const existing = this.model.textBoxes().find(t => hitText(t, point));
        const scale = Math.max(1, this.level - 1);
        const id = existing?.id ?? this.model.addText(Math.round(point.x), Math.round(point.y), this.ink, scale);
        const box = this.model.textBoxes().find(t => t.id === id);
        this.closeTextEditor();

        const area = document.createElement('textarea');
        area.className = 'dd-sketch-editor__text';
        area.value = box.text.toString();
        area.setAttribute('aria-label', 'Sketch text');
        const [a, b, c, d, e, f] = this.matrix();
        const ratio = window.devicePixelRatio || 1;
        area.style.left = `${(a * box.x + c * box.y + e) / ratio}px`;
        area.style.top = `${(b * box.x + d * box.y + f) / ratio}px`;
        area.style.fontSize = `${(8 * box.scale * this.view.scale) / ratio}px`;
        area.addEventListener('input', () => this.model.setText(id, area.value));
        area.addEventListener('blur', () => this.closeTextEditor());
        area.addEventListener('keydown', ev => ev.key === 'Escape' && area.blur());
        this.element.append(area);
        this.textEditor = { area, id };
        setTimeout(() => area.focus(), 0);
    }

    closeTextEditor() {
        if (!this.textEditor) {
            return;
        }

        const { area, id } = this.textEditor;
        this.textEditor = null;
        area.remove();
        if (!this.model.texts.get(id)?.get('text')?.toString().trim()) {
            this.model.removeText(id);
        }
    }

    destroy() {
        this.closeTextEditor();
        this.unlisten();
        this.stopChanges();
        this.stopAnimating();
        this.resizeObserver.disconnect();
        this.awareness.off('change', this.onAwareness);
        this.awareness.setLocalStateField('drawing', null);
        this.awareness.setLocalStateField('pointer', null);
        this.canvas.remove();
    }
}

/** A rectangle or ellipse as an ordinary stroke, so it erases and moves like any line. */
export function shapeStroke(tool, start, end, constrain, ink, level, seed) {
    let [w, h] = [end.x - start.x, end.y - start.y];
    if (constrain) {
        const side = Math.max(Math.abs(w), Math.abs(h));
        [w, h] = [Math.sign(w || 1) * side, Math.sign(h || 1) * side];
    }

    const points = [];
    const add = (x, y) => points.push(round1(x), round1(y), 0.5);
    if (tool === 'rectangle') {
        for (const [x, y] of [[0, 0], [w, 0], [w, h], [0, h], [0, 0]]) {
            add(start.x + x, start.y + y);
        }
    } else {
        const [cx, cy, rx, ry] = [start.x + w / 2, start.y + h / 2, Math.abs(w / 2), Math.abs(h / 2)];
        const steps = Math.max(24, Math.ceil((rx + ry) / 2));
        for (let i = 0; i <= steps; i++) {
            const t = (i / steps) * Math.PI * 2;
            add(cx + rx * Math.cos(t), cy + ry * Math.sin(t));
        }
    }

    // Drawn at the level's own width: a shape has no pressure.
    return { tool: 'pen', ink, level, seed, pointer: 'mouse', points };
}

function textBounds(box) {
    const lines = box.text.toString().split('\n');
    const longest = Math.max(1, ...lines.map(l => l.length));
    return {
        minX: Math.floor(box.x),
        minY: Math.floor(box.y),
        maxX: Math.ceil(box.x + longest * 8 * box.scale),
        maxY: Math.ceil(box.y + lines.length * 8 * box.scale * 1.4),
    };
}

function hitText(box, point) {
    return inside(textBounds(box), point, 2);
}

function inside(bounds, point, slop) {
    return point.x >= bounds.minX - slop && point.x <= bounds.maxX + slop &&
        point.y >= bounds.minY - slop && point.y <= bounds.maxY + slop;
}

function normalizeAngle(angle) {
    const full = Math.PI * 2;
    let a = ((angle % full) + full) % full;
    if (a > Math.PI) {
        a -= full;
    }

    return a;
}

const round1 = v => Math.round(v * 10) / 10;
const round2 = v => Math.round((Number.isFinite(v) ? v : 0.5) * 100) / 100;
