export const chunkLength = 32000;
export function encode(bytes) {
    let value = ''; for (let i = 0; i < bytes.length; i += 8192) value += String.fromCharCode(...bytes.subarray(i, i + 8192));
    return btoa(value);
}
export const decode = value => Uint8Array.from(atob(value), c => c.charCodeAt(0));
export function parts(data) { const result = []; for (let i = 0; i < data.length; i += chunkLength) result.push(data.slice(i, i + chunkLength)); return result; }
export function sequence(a, b) { return BigInt(a) > BigInt(b) ? a : b; }
