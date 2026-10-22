import * as Y from 'yjs';

export const widths = [1, 2, 3, 5, 8];
export function pressureWidth(level, pressure, pointerType) {
    const minimum = widths[Math.max(0, Math.min(4, level))];
    return pointerType === 'pen' ? Math.round(minimum + (8 - minimum) * Math.max(0, Math.min(1, pressure))) : minimum;
}

export function encode(bytes) {
    let text = '';
    for (const byte of bytes) text += String.fromCharCode(byte);
    return btoa(text);
}
export function decode(text) { return Uint8Array.from(atob(text), c => c.charCodeAt(0)); }
export function snapshot(doc) { return encode(Y.encodeStateAsUpdate(doc)); }
export function apply(doc, data, origin) { Y.applyUpdate(doc, decode(data), origin); }

export function stampOffset(seed, index, frame) {
    let n = Math.imul(seed ^ Math.imul(index + 1, 374761393), 668265263) ^ Math.imul(frame + 1, 1274126177);
    n = Math.imul(n ^ (n >>> 13), 1274126177);
    return [(n >>> 0) % 3 - 1, ((n >>> 8) % 3) - 1];
}
