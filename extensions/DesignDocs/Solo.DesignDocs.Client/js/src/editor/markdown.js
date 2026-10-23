// Markdown facts the server never works out for itself: heading slugs, the
// headings sent with each copy, and which link destinations are safe to follow.

import { parser as baseParser, GFM } from '@lezer/markdown';

export const markdownParser = baseParser.configure(GFM);

/**
 * A heading's anchor: lowercase, spaces to '-', nothing but letters, digits,
 * '-' and '_' kept. Only this function decides slugs; the server stores what
 * clients send. Repeats get '-2', '-3' through `slugger`.
 */
export function slug(text) {
    return text
        .toLowerCase()
        .trim()
        .replace(/\s+/g, '-')
        .replace(/[^\p{L}\p{N}_-]/gu, '');
}

/** Slugs for headings in document order, numbering repeats as they come. */
export function slugger() {
    const seen = new Map();
    return text => {
        const base = slug(text);
        const count = seen.get(base) ?? 0;
        seen.set(base, count + 1);
        return count === 0 ? base : `${base}-${count + 1}`;
    };
}

const HEADING = /^(ATXHeading|SetextHeading)(\d)$/;

/** Every heading's level, plain text, slug and position, from a syntax tree or the text alone. */
export function headings(text, tree = markdownParser.parse(text)) {
    const found = [];
    const next = slugger();
    tree.iterate({
        enter(node) {
            const match = HEADING.exec(node.name);
            if (!match) {
                return;
            }

            // The heading's text without its marks: '#'s, the setext underline,
            // and inline emphasis marks.
            let content = '';
            let at = node.from;
            const cursor = node.node.cursor();
            if (cursor.firstChild()) {
                do {
                    if (/Mark$/.test(cursor.name)) {
                        content += text.slice(at, cursor.from);
                        at = cursor.to;
                    } else if (cursor.firstChild()) {
                        do {
                            if (/Mark$/.test(cursor.name)) {
                                content += text.slice(at, cursor.from);
                                at = cursor.to;
                            }
                        } while (cursor.nextSibling());
                        cursor.parent();
                    }
                } while (cursor.nextSibling());
            }

            content += text.slice(at, node.to);
            const plain = content.replace(/\s+/g, ' ').trim();
            if (plain) {
                found.push({ level: Number(match[2]), text: plain, slug: next(plain), from: node.from });
            }

            return false;
        },
    });
    return found;
}

const ALLOWED_SCHEMES = ['http', 'https', 'mailto'];

/**
 * Core's rule (SoloMarkdownPipeline.IsSafeUrl), ported: relative, or http,
 * https or mailto. Pinned to core's by test/fixtures/safe-urls.json, which the
 * host tests run through the C# original.
 */
export function isSafeUrl(url) {
    if (url == null || url.trim() === '') {
        return true;
    }

    const trimmed = url.trim();
    const colon = trimmed.indexOf(':');
    if (colon < 0) {
        return true;
    }

    // "/a:b" and "a/b:c" are relative paths: a separator came before the colon.
    if (/[/?#]/.test(trimmed.slice(0, colon))) {
        return true;
    }

    return ALLOWED_SCHEMES.includes(trimmed.slice(0, colon).toLowerCase());
}
