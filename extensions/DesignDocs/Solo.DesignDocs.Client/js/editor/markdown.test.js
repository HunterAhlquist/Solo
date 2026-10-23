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
test('setext headings and plain labels share the same slug sequence',()=>{assert.deepEqual(headings('Title\n=====\n\n## **Title**\n').map(h=>[h.text,h.slug]),[['Title','title'],['Title','title-2']]);});
test('URL safety matches the shared .NET fixture including Unicode whitespace',async()=>{
    const {readFile}=await import('node:fs/promises');const cases=JSON.parse(await readFile(new URL('../../../../../tests/fixtures/markdown-safe-urls.json',import.meta.url),'utf8'));
    for(const item of cases)assert.equal(isSafeUrl(item.url),item.safe,JSON.stringify(item.url));
});
