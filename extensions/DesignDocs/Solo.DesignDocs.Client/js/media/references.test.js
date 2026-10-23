import test from 'node:test';
import assert from 'node:assert/strict';
import * as Y from 'yjs';
import {mediaReference,mediaRange} from './references.js';

test('viewer removal follows its occurrence after concurrent edits before duplicate embeds',()=>{
    const first=new Y.Doc(),second=new Y.Doc(),text=first.getText('markdown');
    const raw='![[photo.png]]';text.insert(0,raw+' '+raw);Y.applyUpdate(second,Y.encodeStateAsUpdate(first));
    const reference=mediaReference(text,{from:raw.length+1,to:raw.length*2+1,raw});
    second.getText('markdown').insert(0,'Introduction\n');Y.applyUpdate(first,Y.encodeStateAsUpdate(second));
    const range=mediaRange(first,text,reference);assert.ok(range);
    text.delete(range.from,range.to-range.from);
    assert.equal(text.toString(),'Introduction\n'+raw+' ');
    first.destroy();second.destroy();
});
test('stale viewer references cannot remove newly replaced content',()=>{
    const doc=new Y.Doc(),text=doc.getText('markdown'),raw='![[photo.png]]';text.insert(0,raw);
    const reference=mediaReference(text,{from:0,to:raw.length,raw});
    text.delete(0,raw.length);text.insert(0,'Keep this paragraph.');
    assert.equal(mediaRange(doc,text,reference),null);doc.destroy();
});
