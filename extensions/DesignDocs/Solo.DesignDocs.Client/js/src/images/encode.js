// Re-encodes an image in the browser before upload: WebP (JPEG where the
// browser can't encode WebP), long edge at most 2,560 px. That sidesteps
// Safari's uneven HEIC handling and drops location data, since a canvas
// carries no metadata. GIFs pass through untouched, so animations survive.

export const MAX_EDGE = 2560;
export const MAX_BYTES = 10 * 1024 * 1024;

/** @returns {Promise<{bytes: Uint8Array, contentType: string, width: number, height: number, extension: string}>} */
export async function encodeForUpload(blob) {
    const bitmap = await decode(blob);
    const { width, height } = fit(bitmap.width, bitmap.height, MAX_EDGE);

    if (blob.type === 'image/gif' && blob.size <= MAX_BYTES) {
        bitmap.close?.();
        return { bytes: new Uint8Array(await blob.arrayBuffer()), contentType: 'image/gif', width: bitmap.width, height: bitmap.height, extension: '.gif' };
    }

    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    canvas.getContext('2d').drawImage(bitmap, 0, 0, width, height);
    bitmap.close?.();

    for (const [type, extension] of [['image/webp', '.webp'], ['image/jpeg', '.jpg']]) {
        const encoded = await new Promise(resolve => canvas.toBlob(resolve, type, 0.86));
        // Safari before 17 hands back PNG when asked for WebP; take the next choice.
        if (encoded && encoded.type === type) {
            return { bytes: new Uint8Array(await encoded.arrayBuffer()), contentType: type, width, height, extension };
        }
    }

    throw new Error("This browser couldn't re-encode the image.");
}

export function fit(width, height, maxEdge) {
    const scale = Math.min(1, maxEdge / Math.max(width, height));
    return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) };
}

async function decode(blob) {
    if (typeof createImageBitmap === 'function') {
        try {
            return await createImageBitmap(blob, { imageOrientation: 'from-image' });
        } catch {
            // Fall back to an <img>, which decodes more formats on some Safaris.
        }
    }

    const url = URL.createObjectURL(blob);
    try {
        const image = new Image();
        image.src = url;
        await image.decode();
        return image;
    } finally {
        URL.revokeObjectURL(url);
    }
}
