import { presenceTheme } from '../presence.js';
import { textPresence } from './presence.js';
import { EditorState, Compartment } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { yCollab } from 'y-codemirror.next';
import { Provider } from '../sync/provider.js';
import { livePreview } from './preview.js';
import { hangingIndent } from './hanging-indent.js';
import { headings } from './markdown.js';
import { Images } from '../media/images.js';
import { protectMedia, mediaEdit } from './media-protection.js';
import { mediaReference, mediaRange } from '../media/references.js';
import { embeds, imageTargets } from '../media/vault-paths.js';
import { caretPalettePosition } from './palette.js';
import { markdownEditing, formatCommand } from './commands.js';
import { formatState } from './format.js';
import { isSafeUrl } from './markdown.js';
import { panelPosition } from '../sketch/text-panel.js';
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
    function applyAccess(){const editable=provider.canWrite&&!suspended;images.canWrite=editable;view?.dispatch({effects:readOnly.reconfigure([EditorState.readOnly.of(!editable),EditorView.editable.of(editable)])});reportFormat();}
    const noteBody=root.closest('.design-docs-note-body');
    let paletteTimer;
    const palette=noteBody?.querySelector('.design-docs-note-palette');
    // On a phone the palette tucks behind the screen's left edge so the text keeps the width; its tab pops it out
    // over the text, which also closes the keyboard, and tapping back into the note tucks it again.
    const compactPalette=matchMedia('(max-width:600px)');let palettePopped=false;
    const paletteMeasure={
        read(current){
            if(!palette||!noteBody)return null;
            const content=current.contentDOM,style=getComputedStyle(content),line=content.querySelector('.cm-line:not(.design-docs-hanging)');
            const columnLeft=content.getBoundingClientRect().left+parseFloat(style.paddingLeft)+(line?parseFloat(getComputedStyle(line).paddingLeft):0);
            const body=noteBody.getBoundingClientRect();
            const position=caretPalettePosition(current.coordsAtPos(current.state.selection.main.head),body,current.scrollDOM.getBoundingClientRect(),{width:palette.offsetWidth,height:palette.offsetHeight},columnLeft);
            return position&&{...position,fromScreenEdge:body.left+position.left};
        },
        write(position){
            if(!palette)return;const tucked=compactPalette.matches&&!palettePopped;
            palette.toggleAttribute('data-caret-hidden',!position);palette.toggleAttribute('data-tucked',tucked);
            // Tucking only changes the transform, so the slide in and out is one transition; top stays instant so it keeps up with the caret.
            if(position){palette.style.left=position.left+'px';palette.style.top=position.top+'px';palette.style.setProperty('--design-docs-palette-tuck',position.fromScreenEdge+'px');}
        },
    };
    const popPalette=()=>{if(!palette?.hasAttribute('data-tucked'))return;palettePopped=true;view?.contentDOM.blur();positionPalette();};
    const tuckPalette=()=>{if(!palettePopped)return;palettePopped=false;positionPalette();};
    palette?.addEventListener('click',popPalette);
    const positionPalette=()=>view?.requestMeasure(paletteMeasure);
    // The formatting panel is Blazor's markup, shown while the editor reports a
    // selection, so it is placed through custom properties on the note body.
    // It waits for a mouse drag to finish rather than chasing the pointer.
    let lastFormat='null',dragging=false;
    const formatMeasure={
        read(current){
            const range=current.state.selection.main;
            if(!noteBody||lastFormat==='null'||range.empty)return null;
            const start=current.coordsAtPos(range.from,1),end=current.coordsAtPos(range.to,-1);if(!start||!end)return null;
            const body=noteBody.getBoundingClientRect(),scroller=current.scrollDOM.getBoundingClientRect(),content=current.contentDOM.getBoundingClientRect();
            const visible={left:Math.max(body.left,scroller.left),top:Math.max(body.top,scroller.top),right:Math.min(body.right,scroller.right),bottom:Math.min(body.bottom,scroller.bottom)};
            const oneLine=start.bottom>end.top;
            const rect={left:oneLine?start.left:content.left,right:oneLine?end.right:content.right,top:Math.min(start.top,end.top),bottom:Math.max(start.bottom,end.bottom)};
            if(rect.bottom<visible.top||rect.top>visible.bottom)return null;
            const panel=noteBody.querySelector('.design-docs-note-format-palette');
            // Touch screens draw their own selection menu above the text, so go below there.
            const position=panelPosition({left:rect.left-visible.left,top:rect.top-visible.top,bottom:rect.bottom-visible.top,width:rect.right-rect.left},
                {width:visible.right-visible.left,height:visible.bottom-visible.top},{width:panel?.offsetWidth||216,height:panel?.offsetHeight||48},10,matchMedia('(pointer:coarse)').matches);
            return {x:position.x+visible.left-body.left,y:position.y+visible.top-body.top};
        },
        write(position){
            if(!noteBody)return;noteBody.toggleAttribute('data-format-hidden',!position);
            if(position){noteBody.style.setProperty('--design-docs-format-x',position.x+'px');noteBody.style.setProperty('--design-docs-format-y',position.y+'px');}
        },
    };
    const positionFormat=()=>view?.requestMeasure(formatMeasure);
    function reportFormat(){
        if(!view)return;
        const state=provider.canWrite&&!suspended&&!dragging&&view.hasFocus?formatState(view.state):null,message=JSON.stringify(state);
        if(message!==lastFormat){lastFormat=message;bridge.invokeMethodAsync('FormatChanged',message).then(positionFormat,()=>{});}
        positionFormat();
    }
    const dragStart=event=>{if(event.button===0&&event.pointerType==='mouse')dragging=true;};
    const dragEnd=()=>{if(!dragging)return;dragging=false;reportFormat();};
    const showPalette=event=>{if(event.pointerType==='touch'||event.movementX||event.movementY){noteBody?.classList.add('is-pointer-active');positionPalette();clearTimeout(paletteTimer);paletteTimer=setTimeout(()=>noteBody?.classList.remove('is-pointer-active'),2400);}};
    const typing=event=>{if(event.altKey&&event.key==='Insert'){event.preventDefault();noteBody?.classList.add('is-pointer-active');noteBody?.querySelector('.design-docs-note-palette button')?.focus();return;}if(event.key.length===1||event.key==='Backspace'||event.key==='Delete')noteBody?.classList.remove('is-pointer-active');};
    noteBody?.addEventListener('pointermove',showPalette);root.addEventListener('keydown',typing);
    const preview=livePreview(bridge,images);
    view = new EditorView({parent:editorRoot,state:EditorState.create({extensions:[
        EditorView.contentAttributes.of({spellcheck:"true"}),
        ...(needsSpellingFallback(navigator)?[localSpelling(bridge)]:[]),
        ...markdownEditing(),EditorView.updateListener.of(update=>{if(update.selectionSet||update.docChanged||update.geometryChanged||update.viewportChanged)positionPalette();if(update.selectionSet||update.docChanged||update.focusChanged||update.geometryChanged||update.viewportChanged)reportFormat();}),EditorView.lineWrapping,hangingIndent,preview,protectMedia(preview,message=>bridge.invokeMethodAsync('Notice',message)),
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
    view.scrollDOM.addEventListener('scroll',positionPalette);view.scrollDOM.addEventListener('scroll',positionFormat);
    view.contentDOM.addEventListener('pointerdown',dragStart);view.contentDOM.addEventListener('focus',tuckPalette);window.addEventListener('pointerup',dragEnd);
    const paletteResize=new ResizeObserver(()=>{positionPalette();positionFormat();});if(noteBody)paletteResize.observe(noteBody);positionPalette();
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
        format(kind,url){
            if(kind==='link'&&url&&!isSafeUrl(url)){bridge.invokeMethodAsync('Status','Links can only use http, https or mailto addresses.');view.focus();return;}
            formatCommand(kind,url)(view);view.focus();
        },
        focus:()=>view.focus(),
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
        dispose(){removeSaveGuard?.();root.removeEventListener('solo-note-drop',linkDrop);clearTimeout(paletteTimer);paletteResize.disconnect();view.scrollDOM.removeEventListener('scroll',positionPalette);view.scrollDOM.removeEventListener('scroll',positionFormat);window.removeEventListener('pointerup',dragEnd);noteBody?.removeEventListener('pointermove',showPalette);root.removeEventListener('keydown',typing);palette?.removeEventListener('click',popPalette);themeObserver.disconnect();images.dispose();view.destroy();provider.dispose();root.replaceChildren();},
    };
}
