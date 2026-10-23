// Object URLs for a vault's members-only images. The bytes come through .NET
// with the sign-in credential (ISoloClientApi.GetBytesAsync); a plain <img src>
// to the API couldn't send it. One URL per image version, revoked when evicted
// and when the page goes.

export class ImageUrls {
    constructor(fetchBytes, limit = 200) {
        this.fetchBytes = fetchBytes;
        this.limit = limit;
        this.urls = new Map();
    }

    /** A blob: URL for the image's current version. Fetched once per version. */
    get(imageId, version) {
        const key = `${imageId}:${version}`;
        let entry = this.urls.get(key);
        if (entry) {
            // Most recently used last, so eviction takes the oldest.
            this.urls.delete(key);
            this.urls.set(key, entry);
            return entry;
        }

        entry = this.fetchBytes(imageId).then(({ bytes, contentType }) =>
            URL.createObjectURL(new Blob([bytes], { type: contentType || 'application/octet-stream' })));
        entry.catch(() => this.urls.delete(key));
        this.urls.set(key, entry);
        this.evict();
        return entry;
    }

    /** Drops every version of an image, after it was replaced. */
    forget(imageId) {
        for (const [key, entry] of this.urls) {
            if (key.startsWith(`${imageId}:`)) {
                this.urls.delete(key);
                entry.then(url => URL.revokeObjectURL(url), () => {});
            }
        }
    }

    evict() {
        while (this.urls.size > this.limit) {
            const [key, entry] = this.urls.entries().next().value;
            this.urls.delete(key);
            entry.then(url => URL.revokeObjectURL(url), () => {});
        }
    }

    dispose() {
        for (const entry of this.urls.values()) {
            entry.then(url => URL.revokeObjectURL(url), () => {});
        }

        this.urls.clear();
    }
}

/** A stable small angle per image, so a card doesn't twitch between renders. */
export function cardAngle(imageId) {
    let hash = 2166136261;
    for (let i = 0; i < imageId.length; i++) {
        hash = Math.imul(hash ^ imageId.charCodeAt(i), 16777619);
    }

    return (((hash >>> 0) % 500) / 100 - 2.5).toFixed(2);
}
