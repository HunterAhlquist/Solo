import test from 'node:test';
import assert from 'node:assert/strict';
import dictionary from 'dictionary-en';
import { EditorState } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { createSpeller } from './spelling-words.js';
import { needsSpellingFallback,spellingCorrection,mapSpellingTarget } from './spelling.js';

const spell=createSpeller(dictionary);
test('the local checker finds prose errors and offers English corrections without an OS spelling engine',()=>{
    const text='Please recieve the enviroment notes.';
    assert.deepEqual(spell.check(text).map(word=>word.word),['recieve','enviroment']);
    assert.ok(spell.suggest('recieve').includes('receive'));
    assert.deepEqual(spell.check('Please receive the environment notes.'),[]);
});
test('code, URLs, note references, attachment paths, identifiers and task serials are not prose',()=>{
    const text='`recieve`\n\n```cpp\nrecieve enviroment\n```\n\n    recieve\n\n![Plan](attachments/recieve.png) [[recieve|enviroment]] ![[recieve.sketch]] https://recieve.test/enviroment RCQ-VS-123 my_recieve recieveState SHA256';
    assert.deepEqual(spell.check(text),[]);
});
test('only visible text is checked and scrolling can expose a different misspelling',()=>{
    const text='recieve\n\nenviroment';
    assert.deepEqual(spell.check(text,[{from:0,to:7}]).map(word=>word.word),['recieve']);
    assert.deepEqual(spell.check(text,[{from:9,to:text.length}]).map(word=>word.word),['enviroment']);
    assert.deepEqual(spell.check(text,[{from:2,to:5}]),[]);
});
test('ignoring a word clears cached errors but leaves other errors visible',()=>{
    const personal=createSpeller(dictionary);
    assert.equal(personal.check('Quasivault recieve').length,2);
    personal.ignore('Quasivault');
    assert.deepEqual(personal.check('Quasivault recieve').map(word=>word.word),['recieve']);
});
test('iPad desktop user agents and iPhones use the fallback, while desktop and Android keep native spelling',()=>{
    assert.ok(needsSpellingFallback({platform:'MacIntel',maxTouchPoints:5,userAgent:'Mozilla/5.0 (Macintosh) Safari/605.1.15'}));
    assert.ok(needsSpellingFallback({platform:'iPhone',userAgent:'Mozilla/5.0 (iPhone) Safari/604.1'}));
    for(const platform of [{platform:'MacIntel',maxTouchPoints:0,userAgent:'Safari/605.1.15'},{platform:'Win32',maxTouchPoints:10},{platform:'Linux armv8l',userAgent:'Android'}])assert.equal(needsSpellingFallback(platform),false);
});
test('corrections are ordinary editor changes and reject stale text or revoked write access',()=>{
    const state=EditorState.create({doc:'Please recieve notes.'}),target={from:7,to:14,word:'recieve'};
    const change=spellingCorrection(state,target,'receive');
    assert.equal(state.update(change).state.doc.toString(),'Please receive notes.');
    const remote=state.update({changes:{from:7,to:14,insert:'review'}}).state;
    assert.equal(spellingCorrection(remote,target,'receive'),null);
    for(const extension of [EditorState.readOnly.of(true),EditorView.editable.of(false)])assert.equal(spellingCorrection(EditorState.create({doc:state.doc,extensions:[extension]}),target,'receive'),null);
});

test('pending corrections follow remote insertions but reject edits through the target word',()=>{
    const state=EditorState.create({doc:'Please recieve notes.'}),target={from:7,to:14,word:'recieve'};
    const before=state.update({changes:{from:0,insert:'Remote: '}});
    const mapped=mapSpellingTarget(target,before.changes);
    assert.equal(before.state.update(spellingCorrection(before.state,mapped,'receive')).state.doc.toString(),'Remote: Please receive notes.');
    const removed=state.update({changes:{from:0,to:state.doc.length,insert:'Changed elsewhere'}});
    assert.equal(spellingCorrection(removed.state,mapSpellingTarget(target,removed.changes),'receive'),null);
});
