import * as Y from 'yjs';
import {ViewPlugin,Decoration,WidgetType} from '@codemirror/view';
import {StateEffect} from '@codemirror/state';
import {presenceCursor} from '../presence.js';
const refresh=StateEffect.define();
class AvatarCaret extends WidgetType {
    constructor(user){super();this.user=user;}
    eq(other){return JSON.stringify(this.user)===JSON.stringify(other.user);}
    toDOM(){const root=document.createElement('span');root.className='design-docs-text-peer';root.append(presenceCursor('text',this.user));return root;}
    ignoreEvent(){return true;}
}
export function textPresence(provider){return ViewPlugin.fromClass(class{
    constructor(view){this.view=view;this.decorations=this.build(view);this.changed=({added,updated,removed})=>{if([...added,...updated,...removed].some(id=>id!==provider.doc.clientID))view.dispatch({effects:refresh.of(null)});};provider.awareness.on('change',this.changed);}
    build(view){
        const ranges=[],text=provider.doc.getText('markdown');
        for(const [id,state]of provider.awareness.getStates()){
            if(id===provider.doc.clientID||!state.cursor?.head||!state.user)continue;
            const head=Y.createAbsolutePositionFromRelativePosition(Y.createRelativePositionFromJSON(state.cursor.head),provider.doc);
            if(head?.type===text&&head.index<=view.state.doc.length)ranges.push(Decoration.widget({widget:new AvatarCaret(state.user),side:1}).range(head.index));
        }
        return Decoration.set(ranges,true);
    }
    update(update){if(update.docChanged||update.transactions.some(t=>t.effects.some(e=>e.is(refresh))))this.decorations=this.build(update.view);}
    destroy(){provider.awareness.off('change',this.changed);}
},{decorations:value=>value.decorations});}
