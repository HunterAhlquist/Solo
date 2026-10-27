import { presenceTheme } from '../presence.js';
import { textPresence } from './presence.js';
import { EditorState, Compartment } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { yCollab } from 'y-codemirror.next';
import { Provider } from '../sync/provider.js';
import { livePreview } from './preview.js';
import { headings } from './markdown.js';
import { Images } from '../media/images.js';
import { protectMedia, mediaEdit } from './media-protection.js';
import { mediaReference, mediaRange } from '../media/references.js';
import { embeds, imageTargets } from '../media/vault-paths.js';
import { caretPalettePosition } from './palette.js';
import { markdownEditing } from './commands.js';
import { localSpelling,needsSpellingFallback } from './spelling.js';
export function mountEditor(root, bridge, options) {
    const readOnly = new Compartment(); let view,suspended=false;
    presenceTheme(root);
    const fallback = document.createElement('pre'), editorRoot = document.createElement('div');
    editorRoot.className='design-docs-editor-host';fallback.className = 'design-docs-stored-markdown'; root.append(fallback,editorRoot);
    const provider = new Provider({send:(kind,data)=>bridge.invokeMethodAsync('Send',kind,data)}, {
        ...options,
        writable:()=>applyAccess(),
        status:message=>bridge.invokeMethodAsync('Status',message),
        copy:(text,seq)=>bridge.invokeMethodAsync('Copy',text,seq,headings(text)),
        unseeded:markdown=>{fallback.textContent=markdown;editorRoot.hidden=true;bridge.invokeMethodAsync('Ready');},
    });
    const insert=text=>{if(!provider.canWrite)return;const r=view.state.selection.main;view.dispatch({changes:{from:r.from,to:r.to,insert:text},userEvent:'input',selection:{anchor:r.from+text.length}});if(!suspended)view.focus();};
    const text=provider.doc.getText('markdown');
    const images=new Images(bridge,options.canWrite,insert,message=>bridge.invokeMethodAsync('Status',message),{
        icons:options.icons,
        reference:entry=>mediaReference(text,entry),
        text:(from,to)=>view.state.doc.sliceString(from,to),
        removable:reference=>provider.canWrite&&!!mediaRange(provider.doc,text,reference),
        remove:reference=>{
            if(!provider.canWrite)return false;
            const range=mediaRange(provider.doc,text,reference);
            if(!range){bridge.invokeMethodAsync('Status','Attachment changed elsewhere. Reopen the viewer to remove it.');return false;}
            view.dispatch({changes:{...range,insert:''},annotations:mediaEdit.of(true)});return true;
        },
    });
    function applyAccess(){const editable=provider.canWrite&&!suspended;images.canWrite=editable;view?.dispatch({effects:readOnly.reconfigure([EditorState.readOnly.of(!editable),EditorView.editable.of(editable)])});}
    const noteBody=root.closest('.design-docs-note-body');
    let paletteTimer;
    const palette=noteBody?.querySelector('.design-docs-note-palette');
    const paletteMeasure={
        read(current){
            if(!palette||!noteBody)return null;
            const content=current.contentDOM,style=getComputedStyle(content),line=content.querySelector('.cm-line');
            const columnLeft=content.getBoundingClientRect().left+parseFloat(style.paddingLeft)+(line?parseFloat(getComputedStyle(line).paddingLeft):0);
            return caretPalettePosition(current.coordsAtPos(current.state.selection.main.head),noteBody.getBoundingClientRect(),current.scrollDOM.getBoundingClientRect(),{width:palette.offsetWidth,height:palette.offsetHeight},columnLeft);
        },
        write(position){if(!palette)return;palette.toggleAttribute('data-caret-hidden',!position);if(position){palette.style.left=position.left+'px';palette.style.top=position.top+'px';}},
    };
    const positionPalette=()=>view?.requestMeasure(paletteMeasure);
    const showPalette=event=>{if(event.pointerType==='touch'||event.movementX||event.movementY){noteBody?.classList.add('is-pointer-active');positionPalette();clearTimeout(paletteTimer);paletteTimer=setTimeout(()=>noteBody?.classList.remove('is-pointer-active'),2400);}};
    const typing=event=>{if(event.altKey&&event.key==='Insert'){event.preventDefault();noteBody?.classList.add('is-pointer-active');noteBody?.querySelector('.design-docs-note-palette button')?.focus();return;}if(event.key.length===1||event.key==='Backspace'||event.key==='Delete')noteBody?.classList.remove('is-pointer-active');};
    noteBody?.addEventListener('pointermove',showPalette);root.addEventListener('keydown',typing);
    const preview=livePreview(bridge,images);
    view = new EditorView({parent:editorRoot,state:EditorState.create({extensions:[
        EditorView.contentAttributes.of({spellcheck:"true"}),
        ...(needsSpellingFallback(navigator)?[localSpelling(bridge)]:[]),
        ...markdownEditing(),EditorView.updateListener.of(update=>{if(update.selectionSet||update.docChanged||update.geometryChanged||update.viewportChanged)positionPalette();}),EditorView.lineWrapping,preview,protectMedia(preview,message=>bridge.invokeMethodAsync('Notice',message)),
        // Inline presence widgets split words for the native spellchecker.
        yCollab(provider.doc.getText('markdown'),null),textPresence(provider),
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
    const linkDrop=event=>{
        if(!provider.canWrite||suspended)return;
        const {x,y,url,label}=event.detail,position=view.posAtCoords({x,y},false)??view.state.doc.length;
        const safeLabel=label.replace(/[\\[\]]/g,'\\$&');
        const markdown=`[${safeLabel}](${url})`;
        view.dispatch({changes:{from:position,insert:markdown},selection:{anchor:position+markdown.length}});view.focus();
    };
    root.addEventListener('solo-note-drop',linkDrop);
    view.scrollDOM.addEventListener('scroll',positionPalette);
    const paletteResize=new ResizeObserver(positionPalette);if(noteBody)paletteResize.observe(noteBody);positionPalette();
    const previewTheme=()=>{const style=getComputedStyle(root);return style.fontFamily+'|'+style.color+'|'+style.getPropertyValue('--mud-palette-surface');};
    let lastPreviewTheme=previewTheme();
    const themeObserver=new MutationObserver(()=>{const nextTheme=previewTheme();if(nextTheme!==lastPreviewTheme){lastPreviewTheme=nextTheme;images.refresh(path=>/\.sketch$/i.test(path));}presenceTheme(root);});
    themeObserver.observe(document.head,{childList:true,subtree:true,characterData:true});
    themeObserver.observe(document.body,{attributes:true,attributeFilter:['class','style']});
    const removeSaveGuard=globalThis.solo?.offline?.guard(async()=>{if(root.contains(document.activeElement))document.activeElement.blur();return provider.waitForSave();});
    return {
        suspend(value){suspended=!!value;root.inert=suspended;if(suspended&&root.contains(document.activeElement))document.activeElement.blur();applyAccess();},
        connected:async value=>{await provider.connected(value);if(provider.offline)await bridge.invokeMethodAsync('Ready');},
        permissions(value){images.canWrite=value;provider.options.canWrite=value;provider.connected(provider.online);},
        async receive(kind,data) {
            await provider.receive(kind,data);
            if(kind==='state'||kind==='update'){fallback.textContent='';editorRoot.hidden=false;}
            if(kind==='synced')await bridge.invokeMethodAsync('Ready');
            if(kind==='synced' && options.fragment) {
                let target=options.fragment.replace(/^#/,'');try{target=decodeURIComponent(target);}catch{}options.fragment=null;
                const content=provider.doc.getText('markdown').toString(), list=headings(content);
                const index=list.findIndex(h=>h.slug===target);
                if(index<0) bridge.invokeMethodAsync('Status','Heading not found · opened at the top');
                else view.dispatch({effects:EditorView.scrollIntoView(list[index].from,{y:'start'})});
            }
        },
        waitForSave:timeout=>provider.waitForSave(timeout),
        presence:users=>provider.presence(users),
        insert,
        chooseImage:()=>images.choose(),
        sketchesChanged:()=>images.refresh(path=>/\.sketch$/i.test(path)),
        imagesChanged:()=>images.refresh(path=>!/\.sketch$/i.test(path)),
        refreshSketches:names=>images.refresh(path=>names.some(name=>path===name+'.sketch')),
        // For the attachments dialog: what this note embeds now, and acting on one attachment.
        embedTargets:()=>imageTargets(text.toString()),
        view:(path,sketch)=>images.open([{path,sketch}],()=>{},0,()=>null,{remove:false}),
        replaceImage:path=>images.choose(path),
        forget:path=>images.refresh(item=>item===path),
        removeEmbeds(targets){
            if(!provider.canWrite)return;
            const wanted=new Set(targets),changes=embeds(text.toString()).filter(e=>wanted.has(e.raw)).map(e=>({from:e.from,to:e.to,insert:''}));
            if(changes.length)view.dispatch({changes,annotations:mediaEdit.of(true)});
        },
        dispose(){removeSaveGuard?.();root.removeEventListener('solo-note-drop',linkDrop);clearTimeout(paletteTimer);paletteResize.disconnect();view.scrollDOM.removeEventListener('scroll',positionPalette);noteBody?.removeEventListener('pointermove',showPalette);root.removeEventListener('keydown',typing);themeObserver.disconnect();images.dispose();view.destroy();provider.dispose();root.replaceChildren();},
    };
}
