// Draws a sketch: a background pattern layer, an ink layer per boil frame and
// a text layer, at the page's paint-pixel size. Ink is written straight into
// pixel buffers rather than through canvas paths, so every stamp lands on
// whole pixels and nothing is smoothed.

import { BOIL_FRAMES, ERASER_SCALE, boilOffset, brushMask, stamps } from './brush.js';
import { drawPattern } from './page.js';

/** Each ink's shade in a light and a dark scheme. Theme ink is black on light, white on dark. */
export const INK_COLORS = Object.freeze({
    theme: { light: '#1b1b1f', dark: '#f3f3f0' },
    red: { light: '#d0342c', dark: '#ff7b6e' },
    blue: { light: '#2a5bd7', dark: '#7aa5ff' },
    green: { light: '#1f8a4c', dark: '#62d48f' },
});

export const PAGE_COLORS = Object.freeze({ light: '#fbfaf6', dark: '#1f1f24' });
export const PATTERN_COLORS = Object.freeze({ light: '#dcd8cf', dark: '#3b3b43', export: '#d4d4d4' });

export const FONT_FAMILY = 'DesignDocsPixel';

/** The font's design size in paint pixels: Silkscreen is drawn for 8 px. */
export const FONT_SIZE = 8;

let fontLoading = null;

/** Loads the pixel font from the package's content folder, once per page. */
export function loadPixelFont(url) {
    if (!fontLoading && typeof FontFace === 'function' && typeof document !== 'undefined') {
        const face = new FontFace(FONT_FAMILY, `url(${url})`);
        fontLoading = face.load().then(loaded => {
            document.fonts.add(loaded);
            return true;
        }, () => false);
    }

    return fontLoading ?? Promise.resolve(false);
}

export class SketchRenderer {
    /**
     * @param {import('./model.js').SketchModel} model
     * @param {{width: number, height: number, pattern: string}} page
     * @param {{dark?: boolean, exportColors?: boolean, createCanvas?: () => HTMLCanvasElement}} [options]
     */
    constructor(model, page, options = {}) {
        this.model = model;
        this.dark = !!options.dark;
        this.exportColors = !!options.exportColors;
        this.createCanvas = options.createCanvas ?? (() => document.createElement('canvas'));
        this.listeners = new Set();
        this.setPage(page);

        this.onOps = event => {
            // Strokes added at the end only draw on top; anything else redraws.
            const appendedOnly = event.changes.delta.every((d, i) =>
                (i === 0 && d.retain !== undefined) || d.insert !== undefined) &&
                event.changes.delta.some(d => d.insert !== undefined) &&
                !event.changes.delta.some(d => d.delete !== undefined);
            if (appendedOnly && this.frames.every(Boolean)) {
                const added = event.changes.delta.flatMap(d => d.insert ?? []);
                for (let frame = 0; frame < BOIL_FRAMES; frame++) {
                    for (const stroke of added) {
                        this.stamp(this.frames[frame].data, stroke, frame);
                    }

                    this.frames[frame].dirty = true;
                }
            } else {
                this.frames = new Array(BOIL_FRAMES).fill(null);
            }

            this.changed();
        };
        this.onTexts = () => {
            this.textLayer = null;
            this.changed();
        };
        model.ops.observe(this.onOps);
        model.texts.observeDeep(this.onTexts);
    }

    setPage(page) {
        this.width = page.width;
        this.height = page.height;
        this.pattern = page.pattern;
        this.frames = new Array(BOIL_FRAMES).fill(null);
        this.patternLayer = null;
        this.textLayer = null;
        this.changed();
    }

    setDark(dark) {
        if (this.dark !== dark) {
            this.dark = dark;
            this.frames = new Array(BOIL_FRAMES).fill(null);
            this.patternLayer = null;
            this.textLayer = null;
            this.changed();
        }
    }

    /** The font arrived, or anything else outside the doc changed: redraw text. */
    invalidateText() {
        this.textLayer = null;
        this.changed();
    }

    onChange(listener) {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    }

    changed() {
        for (const listener of this.listeners) {
            listener();
        }
    }

    inkColor(ink) {
        const shades = INK_COLORS[ink] ?? INK_COLORS.theme;
        if (this.exportColors) {
            return ink === 'theme' || !INK_COLORS[ink] ? '#000000' : shades.light;
        }

        return this.dark ? shades.dark : shades.light;
    }

    pageColor() {
        return this.exportColors ? '#ffffff' : this.dark ? PAGE_COLORS.dark : PAGE_COLORS.light;
    }

    patternColor() {
        return this.exportColors ? PATTERN_COLORS.export : this.dark ? PATTERN_COLORS.dark : PATTERN_COLORS.light;
    }

    /** The ink layer for a boil frame, as a canvas. */
    inkLayer(frame) {
        let entry = this.frames[frame];
        if (!entry) {
            const canvas = this.canvas();
            const image = canvas.getContext('2d').createImageData(this.width, this.height);
            entry = { canvas, image, data: new Uint32Array(image.data.buffer), dirty: true };
            for (const stroke of this.model.strokes()) {
                this.stamp(entry.data, stroke, frame);
            }

            this.frames[frame] = entry;
        }

        if (entry.dirty) {
            entry.canvas.getContext('2d').putImageData(entry.image, 0, 0);
            entry.dirty = false;
        }

        return entry.canvas;
    }

    patternCanvas() {
        if (!this.patternLayer) {
            this.patternLayer = this.canvas();
            drawPattern(this.patternLayer.getContext('2d'), this.width, this.height, this.pattern, this.patternColor());
        }

        return this.patternLayer;
    }

    /** Text boxes, glyph pixels thresholded so the font stays crisp at every size. */
    textCanvas() {
        if (this.textLayer) {
            return this.textLayer;
        }

        const canvas = this.canvas();
        const context = canvas.getContext('2d');
        context.imageSmoothingEnabled = false;
        for (const box of this.model.textBoxes()) {
            const text = box.text.toString();
            if (!text) {
                continue;
            }

            context.font = `${FONT_SIZE * box.scale}px ${FONT_FAMILY}, monospace`;
            context.textBaseline = 'top';
            context.fillStyle = this.inkColor(box.ink);
            let y = box.y;
            for (const line of text.split('\n')) {
                context.fillText(line, box.x, y);
                y += Math.round(FONT_SIZE * box.scale * 1.4);
            }
        }

        const image = context.getImageData(0, 0, this.width, this.height);
        for (let i = 3; i < image.data.length; i += 4) {
            image.data[i] = image.data[i] >= 110 ? 255 : 0;
        }

        context.putImageData(image, 0, 0);
        this.textLayer = canvas;
        return canvas;
    }

    /** Draws the page as it stands at a boil frame: background, pattern, ink, text. */
    compose(context, frame) {
        context.imageSmoothingEnabled = false;
        context.fillStyle = this.pageColor();
        context.fillRect(0, 0, this.width, this.height);
        context.drawImage(this.patternCanvas(), 0, 0);
        context.drawImage(this.inkLayer(frame), 0, 0);
        context.drawImage(this.textCanvas(), 0, 0);
    }

    /** Stamps one operation into a frame's pixels: ink for a pen, cleared pixels for an eraser. */
    stamp(data, stroke, frame) {
        const eraser = stroke.tool === 'eraser';
        const color = eraser ? 0 : packColor(this.inkColor(stroke.ink));
        const list = stamps(stroke.points, stroke.level, stroke.pointer, eraser ? ERASER_SCALE : 1);
        const { width, height } = this;
        for (let index = 0; index < list.length; index++) {
            const s = list[index];
            const [ox, oy] = eraser ? [0, 0] : boilOffset(stroke.seed, index, frame);
            for (const [mx, my] of brushMask(s.width, s.angle)) {
                const x = s.x + mx + ox;
                const y = s.y + my + oy;
                if (x >= 0 && y >= 0 && x < width && y < height) {
                    data[y * width + x] = color;
                }
            }
        }
    }

    /** A still PNG at one paint pixel per pixel: black ink on white, colored inks in their light shade. */
    async exportPng() {
        const exporter = new SketchRenderer(this.model, { width: this.width, height: this.height, pattern: this.pattern },
            { exportColors: true, createCanvas: this.createCanvas });
        try {
            const canvas = this.canvas();
            exporter.compose(canvas.getContext('2d'), 0);
            return await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
        } finally {
            exporter.destroy();
        }
    }

    canvas() {
        const canvas = this.createCanvas();
        canvas.width = this.width;
        canvas.height = this.height;
        return canvas;
    }

    destroy() {
        this.model.ops.unobserve(this.onOps);
        this.model.texts.unobserveDeep(this.onTexts);
        this.listeners.clear();
    }
}

/** '#rrggbb' as the little-endian RGBA word an ImageData buffer holds. */
export function packColor(hex) {
    const value = parseInt(hex.slice(1), 16);
    const [r, g, b] = [(value >> 16) & 255, (value >> 8) & 255, value & 255];
    return ((255 << 24) | (b << 16) | (g << 8) | r) >>> 0;
}

/** Stamps a stroke in progress straight onto a context, for live previews of what's being drawn. */
export function drawLiveStroke(context, stroke, color, eraserColor) {
    const eraser = stroke.tool === 'eraser';
    context.fillStyle = eraser ? eraserColor : color;
    for (const s of stamps(stroke.points, stroke.level, stroke.pointer, eraser ? ERASER_SCALE : 1)) {
        for (const [mx, my] of brushMask(s.width, s.angle)) {
            context.fillRect(s.x + mx, s.y + my, 1, 1);
        }
    }
}
