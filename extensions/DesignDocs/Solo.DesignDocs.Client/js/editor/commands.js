import {keymap} from '@codemirror/view';
import {defaultKeymap,indentWithTab} from '@codemirror/commands';
import {indentUnit} from '@codemirror/language';
import {markdown,insertNewlineContinueMarkupCommand,deleteMarkupBackward} from '@codemirror/lang-markdown';
import {yUndoManagerKeymap} from 'y-codemirror.next';
import {inlineSyntax,toggleFormat} from './format.js';
export const formatCommand=(kind,url)=>view=>{
    if(view.state.readOnly)return false;
    const spec=toggleFormat(view.state,kind,url);if(!spec)return false;
    view.dispatch(spec);return true;
};
export const markdownEditing=()=>[
    markdown({addKeymap:false,extensions:inlineSyntax}),indentUnit.of('    '),
    keymap.of([{key:'Enter',run:insertNewlineContinueMarkupCommand({nonTightLists:false})},
        {key:'Backspace',run:deleteMarkupBackward},indentWithTab,
        {key:'Mod-b',run:formatCommand('bold')},{key:'Mod-i',run:formatCommand('italic')},{key:'Mod-u',run:formatCommand('underline')},
        ...yUndoManagerKeymap,...defaultKeymap]),
];
