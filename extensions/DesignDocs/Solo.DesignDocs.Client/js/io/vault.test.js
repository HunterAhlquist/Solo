import test from 'node:test';import assert from 'node:assert/strict';import {zipSync} from 'fflate';import * as Y from 'yjs';
import { inspectZip,batches,markdownFromState } from './vault.js';import { resolvePath } from '../media/vault-paths.js';import {encode} from '../sync/protocol.js';
const utf8=new TextEncoder();
test('import keeps BOM, CRLF, front matter and dataview text unchanged',()=>{
    const original='\uFEFF---\r\ntitle: Café\r\n---\r\n# Note\r\n```dataview\r\nLIST\r\n```\r\n';
    const plan=inspectZip(zipSync({'Note.md':utf8.encode(original),'.obsidian/config.json':utf8.encode('{}'),'Board.canvas':utf8.encode('{}'),'Drawing.excalidraw.md':utf8.encode('drawing')}));
    assert.equal(plan.notes.length,1);assert.equal(plan.notes[0].markdown,original);assert.deepEqual(utf8.encode(markdownFromState({seedMarkdown:plan.notes[0].markdown,updates:[]})),utf8.encode(original));assert.equal(plan.skipped.length,3);
});
test('only embedded images import and percent paths resolve',()=>{
    const zip=zipSync({'Folder/Note.md':utf8.encode('![[pictures/a b.png]] ![](../pictures/a%20b.png)'), 'pictures/a b.png':new Uint8Array([1]),'unused.png':new Uint8Array([2])});
    const plan=inspectZip(zip);assert.deepEqual(plan.images.map(i=>i.path),['pictures/a b.png']);assert.ok(plan.skipped.some(s=>s.includes('unused.png')));
});
test('path lookup prefers exact paths and refuses ambiguous basenames',()=>{
    assert.equal(resolvePath(['a/x.png','b/x.png'],'a/x.png'),'a/x.png');assert.equal(resolvePath(['a/x.png','b/x.png'],'x.png'),null);
    assert.equal(resolvePath(['a/x.png'],'x.png'),'a/x.png');assert.equal(resolvePath(['a/x.png'],'../x.png','a/notes/n.md'),'a/x.png');
});
test('notes batch under both the count and body limits',()=>{
    const groups=batches(Array.from({length:201},(_,i)=>({path:`${i}.md`,markdown:'a'})));assert.deepEqual(groups.map(g=>g.length),[100,100,1]);
    const large=batches(Array.from({length:10},(_,i)=>({path:`${i}.md`,markdown:'x'.repeat(1000000)})));assert.ok(large.length>1);
    for(const notes of large)assert.ok(utf8.encode(JSON.stringify({notes})).length<=5*1024*1024);
});
test('export reconstructs exact Yjs text instead of a lagging markdown copy',()=>{
    const doc=new Y.Doc();doc.getText('markdown').insert(0,'Exact\r\ntext\n');
    const state={seedMarkdown:null,updates:[{data:encode(Y.encodeStateAsUpdate(doc))}]};assert.equal(markdownFromState(state),'Exact\r\ntext\n');doc.destroy();
});
test('existing file conflicts are shown before import',()=>{assert.deepEqual(inspectZip(zipSync({'A.md':utf8.encode('A')}),['A.md']).conflicts,['A.md']);});
test('image-like text in code blocks is preserved without importing that image',()=>{
    const plan=inspectZip(zipSync({'A.md':utf8.encode('```dataview\n![[unused.png]]\n```\n'), 'unused.png':new Uint8Array([1])}));assert.equal(plan.images.length,0);assert.ok(plan.notes[0].markdown.includes('![[unused.png]]'));
});
test('external embeds do not accidentally resolve to a local basename',()=>{assert.equal(resolvePath(['x.png'],'https://outside.test/x.png'),null);});
