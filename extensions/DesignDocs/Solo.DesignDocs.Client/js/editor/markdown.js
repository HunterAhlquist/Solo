export function isSafeUrl(url) {
    const text = (url ?? '').trim(), colon = text.indexOf(':');
    return colon < 0 || /[/?#]/u.test(text.slice(0, colon)) || ['http','https','mailto'].includes(text.slice(0, colon).toLowerCase());
}
export function headings(markdown) {
    const used = new Map(), result = []; let fence = null, offset = 0;
    for (const line of markdown.split('\n')) {
        const from = offset; offset += line.length + 1;
        const f = /^\s{0,3}(`{3,}|~{3,})/.exec(line);
        if (f) { if (!fence) fence = f[1][0]; else if (f[1][0] === fence) fence = null; continue; }
        if (fence) continue;
        const h = /^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$/.exec(line); if (!h) continue;
        const text = h[2], base = text.toLowerCase().replace(/\s+/gu,'-').replace(/[^\p{L}\p{N}_-]/gu,'');
        const count = (used.get(base) ?? 0) + 1; used.set(base, count);
        result.push({slug: base + (count > 1 ? `-${count}` : ''), text, level:h[1].length, from});
    }
    return result;
}
