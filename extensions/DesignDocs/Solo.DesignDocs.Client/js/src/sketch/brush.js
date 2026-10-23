// The WigglyPaint look: a small pixel brush stamped at whole-pixel positions
// along each vector stroke, and a gentle boil where each stamp shifts by at
// most one paint pixel between frames. Everything here is deterministic given
// a stroke's seed, so every screen draws the same pixels.

/** Brush widths in paint pixels for the five thickness levels (to tune). */
export const LEVEL_WIDTHS = Object.freeze([1, 2, 3, 5, 8]);

/** Erasers cut wider than the pen at the same level, as erasers do. */
export const ERASER_SCALE = 2;

export const BOIL_FRAMES = 3;

/**
 * Brush width for a level and pressure. With a stylus the chosen level is the
 * minimum and pressure ramps toward the thickest; a mouse reports 0.5 while
 * pressed, so its pressure is ignored, as is a finger's.
 */
export function widthFor(level, pressure, pointerType) {
    const index = Math.min(Math.max(level, 1), LEVEL_WIDTHS.length) - 1;
    const base = LEVEL_WIDTHS[index];
    if (pointerType !== 'pen' || !(pressure > 0)) {
        return base;
    }

    const max = LEVEL_WIDTHS[LEVEL_WIDTHS.length - 1];
    return Math.round(base + (max - base) * Math.min(1, Math.max(0, pressure)));
}

/** A 32-bit hash of the inputs, for seeded choices that look random. */
export function hash(...values) {
    let h = 2166136261;
    for (const value of values) {
        h = Math.imul(h ^ (value | 0), 16777619);
        h ^= h >>> 13;
        h = Math.imul(h, 0x5bd1e995);
        h ^= h >>> 15;
    }

    return h >>> 0;
}

/**
 * A stamp's offset in one boil frame: -1, 0 or 1 paint pixels on each axis.
 * Frame 0 is the still frame (export, reduced motion), with no offsets.
 */
export function boilOffset(seed, index, frame) {
    if (frame === 0) {
        return [0, 0];
    }

    const h = hash(seed, index, frame);
    // Two of three stamps hold still in any frame; the rest move by one pixel.
    if (h % 3 !== 0) {
        return [0, 0];
    }

    return [((h >>> 4) % 3) - 1, ((h >>> 8) % 3) - 1];
}

/**
 * Where stamps land along a stroke: whole-pixel positions about a third of a
 * brush apart, each with its width and a direction bucket that picks one of
 * the pre-drawn brush angles.
 *
 * @param {number[]} points  Flat [x, y, pressure, x, y, pressure, ...] in paint pixels.
 * @returns {{x: number, y: number, width: number, angle: number}[]}
 */
export function stamps(points, level, pointerType = 'pen', scale = 1) {
    const result = [];
    const count = Math.floor(points.length / 3);
    if (count === 0) {
        return result;
    }

    const widthAt = i => Math.max(1, Math.round(widthFor(level, points[i * 3 + 2], pointerType) * scale));
    let lastX = null;
    let lastY = null;
    const push = (x, y, width, angle) => {
        const px = Math.round(x);
        const py = Math.round(y);
        if (px !== lastX || py !== lastY) {
            result.push({ x: px, y: py, width, angle });
            lastX = px;
            lastY = py;
        }
    };

    push(points[0], points[1], widthAt(0), 0);
    let carry = 0;
    for (let i = 1; i < count; i++) {
        const [x0, y0, x1, y1] = [points[(i - 1) * 3], points[(i - 1) * 3 + 1], points[i * 3], points[i * 3 + 1]];
        const length = Math.hypot(x1 - x0, y1 - y0);
        if (length === 0) {
            continue;
        }

        const w0 = widthAt(i - 1);
        const w1 = widthAt(i);
        const angle = angleBucket(x1 - x0, y1 - y0);
        let at = carry;
        while (at <= length) {
            const t = at / length;
            const width = Math.round(w0 + (w1 - w0) * t);
            push(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, width, angle);
            at += Math.max(1, width / 3);
        }

        carry = at - length;
    }

    return result;
}

/** One of four direction buckets (0°, 45°, 90°, 135°), so stamps never need rotating. */
export function angleBucket(dx, dy) {
    const degrees = ((Math.atan2(dy, dx) * 180) / Math.PI + 180) % 180;
    return Math.round(degrees / 45) % 4;
}

const masks = new Map();

/**
 * The pixels one stamp covers, relative to its center: a slightly squashed
 * disk, pre-drawn at four angles, so a stroke has a hand-made edge.
 * @returns {Array<[number, number]>}
 */
export function brushMask(width, angle) {
    const key = `${width}:${angle}`;
    let mask = masks.get(key);
    if (mask) {
        return mask;
    }

    mask = [];
    if (width <= 1) {
        mask.push([0, 0]);
    } else if (width === 2) {
        mask.push([0, 0], [1, 0], [0, 1], [1, 1]);
    } else {
        const a = width / 2;
        const b = width / 2 * 0.78;
        const theta = (angle * 45 * Math.PI) / 180;
        const [cos, sin] = [Math.cos(theta), Math.sin(theta)];
        const reach = Math.ceil(a);
        for (let dy = -reach; dy <= reach; dy++) {
            for (let dx = -reach; dx <= reach; dx++) {
                const u = dx * cos + dy * sin;
                const v = -dx * sin + dy * cos;
                if ((u * u) / (a * a) + (v * v) / (b * b) <= 1.0) {
                    mask.push([dx, dy]);
                }
            }
        }
    }

    masks.set(key, mask);
    return mask;
}
