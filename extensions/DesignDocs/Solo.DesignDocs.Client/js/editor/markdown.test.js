import test from 'node:test';
import assert from 'node:assert/strict';
import { isSafeUrl, headings } from './markdown.js';
test('URL rules match core scheme and relative-path rules',()=>{
    for(const url of ['',null,'/docs/a:b','a/b:c','#a:b','?a:b','https://a.test','HTTP://a','mailto:a@b'])assert.equal(isSafeUrl(url),true,url);
    for(const url of ['javascript:alert(1)','data:image/svg+xml,a','file:///tmp/a','vbscript:a','java\nscript:a','a\\b:c'])assert.equal(isSafeUrl(url),false,url);
});
test('heading slugs preserve Unicode, suffix duplicates and skip code fences',()=>{
    assert.deepEqual(headings('# Hello world\n## Hello world\n# Café!\n```\n# code\n```').map(h=>h.slug),['hello-world','hello-world-2','café']);
});
