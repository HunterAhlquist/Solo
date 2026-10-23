import {keymap} from '@codemirror/view';
import {defaultKeymap,indentWithTab} from '@codemirror/commands';
import {indentUnit} from '@codemirror/language';
import {markdown,insertNewlineContinueMarkupCommand,deleteMarkupBackward} from '@codemirror/lang-markdown';
import {yUndoManagerKeymap} from 'y-codemirror.next';
export const markdownEditing=()=>[
    markdown({addKeymap:false}),indentUnit.of('    '),
    keymap.of([{key:'Enter',run:insertNewlineContinueMarkupCommand({nonTightLists:false})},
        {key:'Backspace',run:deleteMarkupBackward},indentWithTab,...yUndoManagerKeymap,...defaultKeymap]),
];
