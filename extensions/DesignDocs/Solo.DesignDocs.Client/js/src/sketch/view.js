// What every sketch on screen shares: the boil ticker, and the read-only
// preview a note shows where it embeds a sketch.

import { BOIL_FRAMES } from './brush.js';
import { drawLiveStroke } from './render.js';

const FRAME_MS = 150;
const views = new Set();
let timer = null;
let frame = 0;

const reducedMotion = () =>
    typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;

/** The boil frame everyone draws now: always 0 under reduced motion, which holds the drawing still. */
export const currentFrame = () => (reducedMotion() ? 0 : frame);

/** Animates a view while it's on screen and the tab is visible. Returns the stop function. */
export function animate(view) {
    views.add(view);
    schedule();
    return () => {
        views.delete(view);
        schedule();
    };
}

function schedule() {
    const wanted = views.size > 0 && !document.hidden && !reducedMotion() &&
        [...views].some(v => v.visible !== false);
    if (wanted && timer === null) {
        timer = setInterval(() => {
            frame = (frame + 1) % BOIL_FRAMES;
            for (const view of views) {
                if (view.visible !== false) {
                    view.draw();
                }
            }
        }, FRAME_MS);
    } else if (!wanted && timer !== null) {
        clearInterval(timer);
        timer = null;
    }
}

if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', schedule);
}

/** Watches whether an element is on screen, so off-screen sketches stop boiling. */
export function watchVisibility(element, view) {
    if (typeof IntersectionObserver !== 'function') {
        return () => {};
    }

    const observer = new IntersectionObserver(entries => {
        view.visible = entries.some(e => e.isIntersecting);
        schedule();
        if (view.visible) {
            view.draw();
        }
    });
    observer.observe(element);
    return () => observer.disconnect();
}

/**
 * A sketch inside a note: drawn at one paint pixel per canvas pixel and scaled
 * by CSS with pixels kept square. Shows others' strokes as they draw them.
 */
export class SketchPreview {
    constructor(element, renderer, awareness) {
        this.renderer = renderer;
        this.awareness = awareness;
        this.visible = true;
        this.canvas = document.createElement('canvas');
        this.canvas.className = 'dd-sketch-canvas';
        element.append(this.canvas);
        this.resize();

        this.stopChanges = renderer.onChange(() => {
            this.resize();
            this.draw();
        });
        this.onAwareness = () => this.draw();
        awareness?.on('change', this.onAwareness);
        this.stopAnimating = animate(this);
        this.stopWatching = watchVisibility(element, this);
        this.draw();
    }

    resize() {
        if (this.canvas.width !== this.renderer.width || this.canvas.height !== this.renderer.height) {
            this.canvas.width = this.renderer.width;
            this.canvas.height = this.renderer.height;
            this.canvas.style.aspectRatio = `${this.renderer.width} / ${this.renderer.height}`;
        }
    }

    draw() {
        const context = this.canvas.getContext('2d');
        this.renderer.compose(context, currentFrame());
        if (!this.awareness) {
            return;
        }

        this.awareness.getStates().forEach((state, clientId) => {
            if (clientId !== this.awareness.clientID && state?.drawing) {
                drawLiveStroke(context, state.drawing, this.renderer.inkColor(state.drawing.ink), this.renderer.pageColor());
            }
        });
    }

    destroy() {
        this.stopChanges();
        this.stopAnimating();
        this.stopWatching();
        this.awareness?.off('change', this.onAwareness);
        this.canvas.remove();
    }
}
