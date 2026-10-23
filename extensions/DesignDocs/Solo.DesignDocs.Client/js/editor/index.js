import { EditorState, Compartment } from '@codemirror/state';
import { EditorView, keymap } from '@codemirror/view';
import { defaultKeymap } from '@codemirror/commands';
import { markdown } from '@codemirror/lang-markdown';
import { yCollab, yUndoManagerKeymap } from 'y-codemirror.next';
import { Provider } from '../sync/provider.js';
import { livePreview, refreshSketchPreview } from './preview.js';
import { headings } from './markdown.js';
import { Images } from '../media/images.js';
import { protectMedia, mediaEdit } from './media-protection.js';
import { mediaReference, mediaRange } from '../media/references.js';
import { caretPalettePosition } from './palette.js';
export function mountEditor(root, bridge, options) {
    const readOnly = new Compartment(); let view;
    const baseUser={...options.user};
    function themeUser(){const probe=document.createElement('span');probe.style.color='var(--mud-palette-background, white)';root.append(probe);const rgb=getComputedStyle(probe).color.match(/[\d.]+/g)?.slice(0,3).map(Number)??[255,255,255];probe.remove();const light=rgb.reduce((a,b)=>a+b,0)<384?'75%':'50%';return {...baseUser,color:baseUser.color.replace(/55%/,light),colorLight:baseUser.colorLight.replace(/55%/,light)};}
    options.user=themeUser();
    const fallback = document.createElement('pre'), editorRoot = document.createElement('div');
    editorRoot.className='design-docs-editor-host';fallback.className = 'design-docs-stored-markdown'; root.append(fallback,editorRoot);
    const provider = new Provider({send:(kind,data)=>bridge.invokeMethodAsync('Send',kind,data)}, {
        ...options,
        writable:value=>{images.canWrite=value;view?.dispatch({effects:readOnly.reconfigure([EditorState.readOnly.of(!value),EditorView.editable.of(value)])});},
        status:message=>bridge.invokeMethodAsync('Status',message),
        copy:(text,seq)=>bridge.invokeMethodAsync('Copy',text,seq,headings(text)),
        unseeded:markdown=>{fallback.textContent=markdown;editorRoot.hidden=true;},
    });
    const insert=text=>{if(!provider.canWrite)return;const r=view.state.selection.main;view.dispatch({changes:{from:r.from,to:r.to,insert:text},selection:{anchor:r.from+text.length}});view.focus();};
    const text=provider.doc.getText('markdown');
    const images=new Images(bridge,options.canWrite,insert,message=>bridge.invokeMethodAsync('Status',message),{
        icons:options.icons,
        reference:entry=>mediaReference(text,entry),
        text:(from,to)=>view.state.doc.sliceString(from,to),
        remove:reference=>{
            if(!provider.canWrite)return false;
            const range=mediaRange(provider.doc,text,reference);
            if(!range){bridge.invokeMethodAsync('Status','Attachment changed elsewhere. Reopen the viewer to remove it.');return false;}
            view.dispatch({changes:{...range,insert:''},annotations:mediaEdit.of(true)});return true;
        },
    });
    const noteBody=root.closest('.design-docs-note-body');
    let paletteTimer;
    const palette=noteBody?.querySelector('.design-docs-note-palette');
    const paletteMeasure={
        read(current){
            if(!palette||!noteBody)return null;
            return caretPalettePosition(current.coordsAtPos(current.state.selection.main.head),noteBody.getBoundingClientRect(),current.scrollDOM.getBoundingClientRect(),{width:palette.offsetWidth,height:palette.offsetHeight});
        },
        write(position){if(!palette)return;palette.toggleAttribute('data-caret-hidden',!position);if(position){palette.style.left=position.left+'px';palette.style.top=position.top+'px';}},
    };
    const positionPalette=()=>view?.requestMeasure(paletteMeasure);
    const showPalette=event=>{if(event.pointerType==='touch'||event.movementX||event.movementY){noteBody?.classList.add('is-pointer-active');positionPalette();clearTimeout(paletteTimer);paletteTimer=setTimeout(()=>noteBody?.classList.remove('is-pointer-active'),2400);}};
    const typing=event=>{if(event.altKey&&event.key==='Insert'){event.preventDefault();noteBody?.classList.add('is-pointer-active');noteBody?.querySelector('.design-docs-note-palette button')?.focus();return;}if(event.key.length===1||event.key==='Backspace'||event.key==='Delete')noteBody?.classList.remove('is-pointer-active');};
    noteBody?.addEventListener('pointermove',showPalette);root.addEventListener('keydown',typing);
    const preview=livePreview(bridge,images);
    view = new EditorView({parent:editorRoot,state:EditorState.create({extensions:[
        markdown(),EditorView.updateListener.of(update=>{if(update.selectionSet||update.docChanged||update.geometryChanged||update.viewportChanged)positionPalette();}),keymap.of([...defaultKeymap,...yUndoManagerKeymap]),EditorView.lineWrapping,preview,protectMedia(preview,message=>bridge.invokeMethodAsync('Status',message)),
        yCollab(provider.doc.getText('markdown'),provider.awareness),
        readOnly.of([EditorState.readOnly.of(true),EditorView.editable.of(false)]),
        EditorView.domEventHandlers({focus:()=>{bridge.invokeMethodAsync('Focus',true);},blur:()=>{bridge.invokeMethodAsync('Focus',false);},
            paste:event=>{const files=[...(event.clipboardData?.files??[])];if(!provider.canWrite||!files.length)return false;event.preventDefault();files.forEach(f=>images.upload(f));return true;},
            drop:event=>{
                if(event.dataTransfer?.types.includes('application/x-solo-design-doc-media')){
                    event.preventDefault();
                    if(!provider.canWrite||!images.dragged)return true;
                    const range=mediaRange(provider.doc,text,images.dragged),position=view.posAtCoords({x:event.clientX,y:event.clientY})??view.state.doc.length;
                    if(!range||position>=range.from&&position<=range.to)return true;
                    let raw=images.dragged.raw;
                    if(position>0&&!/\s/.test(view.state.doc.sliceString(position-1,position)))raw=' '+raw;
                    if(position<view.state.doc.length&&!/\s/.test(view.state.doc.sliceString(position,position+1)))raw+=' ';
                    view.dispatch({changes:[{...range,insert:''},{from:position,insert:raw}],annotations:mediaEdit.of(true)});images.dragged=null;return true;
                }
                const files=[...(event.dataTransfer?.files??[])];if(!provider.canWrite||!files.length)return false;event.preventDefault();files.forEach(f=>images.upload(f));return true;},
            dragover:event=>{if(event.dataTransfer?.types.some(type=>type==='Files'||type==='application/x-solo-design-doc-media'))event.preventDefault();},
        }),

    ]})});
    view.scrollDOM.addEventListener('scroll',positionPalette);
    const paletteResize=new ResizeObserver(positionPalette);if(noteBody)paletteResize.observe(noteBody);positionPalette();
    const previewTheme=()=>{const style=getComputedStyle(root);return style.fontFamily+'|'+style.color+'|'+style.getPropertyValue('--mud-palette-surface');};
    let lastPreviewTheme=previewTheme();
    const themeObserver=new MutationObserver(()=>{const nextTheme=previewTheme();if(nextTheme!==lastPreviewTheme){lastPreviewTheme=nextTheme;images.refresh();}const user=themeUser();if(user.color!==provider.options.user.color){provider.options.user=user;if(provider.canWrite)provider.awareness.setLocalStateField('user',user);}});
    themeObserver.observe(document.head,{childList:true,subtree:true,characterData:true});
    return {
        connected:value=>provider.connected(value),
        permissions(value){images.canWrite=value;provider.options.canWrite=value;provider.connected(provider.online);},
        async receive(kind,data) {
            await provider.receive(kind,data);
            if(kind==='state'||kind==='update'){fallback.textContent='';editorRoot.hidden=false;}
            if(kind==='synced' && options.fragment) {
                let target=options.fragment.replace(/^#/,'');try{target=decodeURIComponent(target);}catch{}options.fragment=null;
                const content=provider.doc.getText('markdown').toString(), list=headings(content);
                const index=list.findIndex(h=>h.slug===target);
                if(index<0) bridge.invokeMethodAsync('Status','Heading not found · opened at the top');
                else view.dispatch({effects:EditorView.scrollIntoView(list[index].from,{y:'start'})});
            }
        },
        presence:users=>provider.presence(users),
        insert,
        chooseImage:()=>images.choose(),
        imagesChanged:()=>images.refresh(),
        refreshSketches:()=>images.refresh(),
        dispose(){clearTimeout(paletteTimer);paletteResize.disconnect();view.scrollDOM.removeEventListener('scroll',positionPalette);noteBody?.removeEventListener('pointermove',showPalette);root.removeEventListener('keydown',typing);themeObserver.disconnect();images.dispose();view.destroy();provider.dispose();root.replaceChildren();},
    };
}
