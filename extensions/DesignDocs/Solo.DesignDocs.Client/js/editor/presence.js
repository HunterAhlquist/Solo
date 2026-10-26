import * as Y from 'yjs';
import {ViewPlugin,RectangleMarker,layer} from '@codemirror/view';
import {StateEffect,EditorSelection} from '@codemirror/state';
import {presenceCursor} from '../presence.js';
const refresh=StateEffect.define();
export function textPeers(provider,length){
    const peers=[],text=provider.doc.getText('markdown');
    for(const [id,state]of provider.awareness.getStates()){
        if(id===provider.doc.clientID||!state.cursor?.anchor||!state.cursor.head||!state.user)continue;
        const anchor=Y.createAbsolutePositionFromRelativePosition(Y.createRelativePositionFromJSON(state.cursor.anchor),provider.doc);
        const head=Y.createAbsolutePositionFromRelativePosition(Y.createRelativePositionFromJSON(state.cursor.head),provider.doc);
        if(anchor?.type===text&&head?.type===text&&anchor.index<=length&&head.index<=length)peers.push({anchor:anchor.index,head:head.index,user:state.user});
    }
    return peers;
}
class PeerMarker extends RectangleMarker{
    constructor(rect,user,caret){super(caret?'design-docs-text-peer':'design-docs-selection-peer',rect.left,rect.top,rect.width,rect.height);this.user=user;this.caret=caret;this.key=JSON.stringify([caret,user]);}
    eq(other){return this.key===other.key&&super.eq(other);}
    draw(){
        const dom=super.draw();
        if(this.caret){dom.style.fontSize=this.height/1.35+'px';dom.append(presenceCursor('text',this.user));}
        else dom.style.backgroundColor=this.user.colorLight??`color-mix(in srgb, ${this.user.color??'#30bced'} 20%, transparent)`;
        return dom;
    }
    update(dom,previous){
        if(this.key!==previous.key||!super.update(dom,previous))return false;
        if(this.caret)dom.style.fontSize=this.height/1.35+'px';return true;
    }
}
export function textPresence(provider){
    const text=provider.doc.getText('markdown');
    const overlay=caret=>layer({above:caret,class:caret?'design-docs-peer-carets':'design-docs-peer-selections',
        update:update=>update.docChanged||update.viewportChanged||update.transactions.some(t=>t.effects.some(e=>e.is(refresh))),
        markers:view=>textPeers(provider,view.state.doc.length).flatMap(peer=>{
            if(!caret&&peer.anchor===peer.head)return [];
            const range=caret?EditorSelection.cursor(peer.head):EditorSelection.range(peer.anchor,peer.head);
            return RectangleMarker.forRange(view,'',range).map(rect=>new PeerMarker(rect,peer.user,caret));
        })
    });
    return ViewPlugin.fromClass(class{
        constructor(view){
            this.view=view;this.changed=({added,updated,removed})=>{
                const clients=[...added,...updated,...removed];
                if(clients.includes(provider.doc.clientID))this.publish();
                if(clients.some(id=>id!==provider.doc.clientID))view.dispatch({effects:refresh.of(null)});
            };
            provider.awareness.on('change',this.changed);this.publish();
        }
        publish(){
            const local=provider.awareness.getLocalState();if(!local)return;
            if(!this.view.hasFocus){
                if(local.cursor)provider.awareness.setLocalStateField('cursor',null);return;
            }
            const selection=this.view.state.selection.main,anchor=Y.createRelativePositionFromTypeIndex(text,selection.anchor),head=Y.createRelativePositionFromTypeIndex(text,selection.head);
            if(!local.cursor||!Y.compareRelativePositions(Y.createRelativePositionFromJSON(local.cursor.anchor),anchor)||!Y.compareRelativePositions(Y.createRelativePositionFromJSON(local.cursor.head),head))provider.awareness.setLocalStateField('cursor',{anchor,head});
        }
        update(update){if(update.docChanged||update.selectionSet||update.focusChanged)this.publish();}
        destroy(){provider.awareness.off('change',this.changed);if(provider.awareness.getLocalState()?.cursor)provider.awareness.setLocalStateField('cursor',null);}
    },{provide:()=>[overlay(false),overlay(true)]});
}
