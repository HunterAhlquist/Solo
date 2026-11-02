import test from 'node:test';import assert from 'node:assert/strict';
import {EditorState} from '@codemirror/state';import {syntaxTree} from '@codemirror/language';
import {markdownEditing} from './commands.js';import {formatState,toggleFormat} from './format.js';
// `{` and `}` in the fixture mark the selection.
function editor(marked){
    const anchor=marked.indexOf('{'),doc=marked.replace('{','').replace('}',''),head=marked.indexOf('}')-1;
    return EditorState.create({doc,selection:{anchor,head},extensions:markdownEditing()});
}
function toggle(state,kind,url){return state.update(toggleFormat(state,kind,url)).state;}
const shown=state=>{const {from,to}=state.selection.main,doc=state.doc.toString();return doc.slice(0,from)+'{'+doc.slice(from,to)+'}'+doc.slice(to);};
test('a caret reports no formatting',()=>{assert.equal(formatState(EditorState.create({doc:'text',extensions:markdownEditing()})),null);});
test('each style wraps the selection and keeps the text selected',()=>{
    for(const [kind,expected] of [['bold','a **{word}** b'],['italic','a *{word}* b'],['highlight','a =={word}== b'],['link','a [{word}](https://x.test) b']]){
        const state=toggle(editor('a {word} b'),kind,'https://x.test');
        assert.equal(shown(state),expected,kind);assert.equal(formatState(state)[kind],true,kind);
    }
});
test('toggling a style that covers the selection removes it',()=>{
    for(const marked of ['**{word}**','{**word**}','_{word}_','=={word}==','[{word}](https://x.test)']){
        const state=editor(marked),kind=Object.entries(formatState(state)).find(([,on])=>on)[0];
        assert.equal(toggle(state,kind).doc.toString(),'word',marked);
    }
});
test('turning a style off inside a longer run splits the run',()=>{
    assert.equal(shown(toggle(editor('**one {two} three**'),'bold')),'**one **{two}** three**');
    assert.equal(toggle(editor('[one {two} three](u)'),'link').doc.toString(),'[one ](u)two[ three](u)');
});
test('styling across an existing run absorbs it rather than nesting',()=>{
    assert.equal(toggle(editor('{a **b** c}'),'bold').doc.toString(),'**a b c**');
    assert.equal(toggle(editor('**a {b** c}'),'bold').doc.toString(),'**a b c**');
});
test('surrounding whitespace and block markup stay outside the markup',()=>{
    assert.equal(toggle(editor('{- one \n- two}'),'highlight').doc.toString(),'- ==one== \n- ==two==');
    assert.equal(toggle(editor('{# Title}'),'italic').doc.toString(),'# *Title*');
});
test('a multi-line selection is styled only when every line is',()=>{
    const state=editor('{**one**\ntwo}');assert.equal(formatState(state).bold,false);
    assert.equal(toggle(state,'bold').doc.toString(),'**one**\n**two**');
});
test('styles nest',()=>{
    const state=toggle(toggle(editor('{word}'),'bold'),'italic');
    assert.deepEqual(formatState(state),{bold:true,italic:true,highlight:false,link:false});
    assert.equal(toggle(state,'bold').doc.toString(),'*word*');
});
test('link targets with spaces or parentheses are wrapped in angle brackets',()=>{
    assert.equal(toggle(editor('{a}'),'link','https://x.test/a (b)').doc.toString(),'[a](<https://x.test/a (b)>)');
});
test('highlight needs non-space text on both sides and leaves === alone',()=>{
    const names=doc=>{const list=[];syntaxTree(EditorState.create({doc,extensions:markdownEditing()})).iterate({enter:n=>{list.push(n.name);}});return list;};
    assert.ok(names('==a==').includes('Highlight'));
    for(const doc of ['== a ==','===a===','a == b'])assert.ok(!names(doc).includes('Highlight'),doc);
});
