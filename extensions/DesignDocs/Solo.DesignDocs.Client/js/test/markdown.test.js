import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { headings, isSafeUrl, slugger } from '../src/editor/markdown.js';

const load = name => JSON.parse(readFileSync(new URL(`./fixtures/${name}`, import.meta.url), 'utf8'));

test('slugs match the fixture, with repeats numbered', () => {
    const next = slugger();
    for (const [text, expected] of load('slugs.json').cases) {
        assert.equal(next(text), expected, text);
    }
});

test('isSafeUrl agrees with core on every case', () => {
    for (const [url, expected] of load('safe-urls.json').cases) {
        assert.equal(isSafeUrl(url), expected, JSON.stringify(url));
    }
});

test('headings come out in order with levels and positions', () => {
    const text = '# Title\n\nIntro\n\n## Combat\n\nSetext\n---\n\n```\n# not a heading\n```\n\n## Combat\n';
    const found = headings(text);
    assert.deepEqual(found.map(h => [h.level, h.text, h.slug]), [
        [1, 'Title', 'title'],
        [2, 'Combat', 'combat'],
        [2, 'Setext', 'setext'],
        [2, 'Combat', 'combat-2'],
    ]);
    assert.equal(text.slice(found[1].from, found[1].from + 9), '## Combat');
});
