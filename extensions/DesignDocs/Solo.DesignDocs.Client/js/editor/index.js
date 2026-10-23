import { EditorState, Compartment } from '@codemirror/state';
import { EditorView, keymap, lineNumbers, highlightActiveLineGutter } from '@codemirror/view';
import { defaultKeymap } from '@codemirror/commands';
import { markdown } from '@codemirror/lang-markdown';
import { yCollab, yUndoManagerKeymap } from 'y-codemirror.next';
import { Provider } from '../sync/provider.js';
import { livePreview, refreshSketchPreview } from './preview.js';
import { headings } from './markdown.js';
import { Images } from '../media/images.js';
import { mediaReference, mediaRange } from '../media/references.js';
export function mountEditor(root, bridge, options) {
    const readOnly = new Compartment(); let view;
    const baseUser={...options.user};
    function themeUser(){const probe=document.createElement('span');probe.style.color='var(--mud-palette-background, white)';root.append(probe);const rgb=getComputedStyle(probe).color.match(/[\d.]+/g)?.slice(0,3).map(Number)??[255,255,255];probe.remove();const light=rgb.reduce((a,b)=>a+b,0)<384?'75%':'50%';return {...baseUser,color:baseUser.color.replace(/55%/,light),colorLight:baseUser.colorLight.replace(/55%/,light)};}
    options.user=themeUser();
    const fallback = document.createElement('pre'), editorRoot = document.createElement('div');
    editorRoot.className='design-docs-editor-host';fallback.className = 'design-docs-stored-markdown'; root.append(fallback,editorRoot);
    const provider = new Provider({send:(kind,data)=>bridge.invokeMethodAsync('Send',kind,data)}, {
        ...options,
        writable:value=>{images.canWrite=value;upload.disabled=!value;view?.dispatch({effects:readOnly.reconfigure([EditorState.readOnly.of(!value),EditorView.editable.of(value)])});},
        status:message=>bridge.invokeMethodAsync('Status',message),
        copy:(text,seq)=>bridge.invokeMethodAsync('Copy',text,seq,headings(text)),
        unseeded:markdown=>{fallback.textContent=markdown;editorRoot.hidden=true;},
    });
    const insert=text=>{if(!provider.canWrite)return;const r=view.state.selection.main;view.dispatch({changes:{from:r.from,to:r.to,insert:text},selection:{anchor:r.from+text.length}});view.focus();};
    const text=provider.doc.getText('markdown');
    const images=new Images(bridge,options.canWrite,insert,message=>bridge.invokeMethodAsync('Status',message),{
        reference:entry=>mediaReference(text,entry),
        text:(from,to)=>view.state.doc.sliceString(from,to),
        remove:reference=>{
            if(!provider.canWrite)return false;
            const range=mediaRange(provider.doc,text,reference);
            if(!range){bridge.invokeMethodAsync('Status','Attachment changed elsewhere. Reopen the viewer to remove it.');return false;}
            view.dispatch({changes:{...range,insert:''}});return true;
        },
    });
    const toolbar=document.createElement('div');toolbar.className='design-docs-media-tools';
    const upload=document.createElement('button');upload.type='button';upload.textContent='Add image';upload.onclick=()=>images.choose();toolbar.append(upload);
    root.prepend(toolbar);toolbar.hidden=!options.canWrite;
    view = new EditorView({parent:editorRoot,state:EditorState.create({extensions:[
        markdown(),lineNumbers(),highlightActiveLineGutter(),keymap.of([...defaultKeymap,...yUndoManagerKeymap]),EditorView.lineWrapping,livePreview(bridge,images),
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
                    view.dispatch({changes:[{...range,insert:''},{from:position,insert:raw}]});images.dragged=null;return true;
                }
                const files=[...(event.dataTransfer?.files??[])];if(!provider.canWrite||!files.length)return false;event.preventDefault();files.forEach(f=>images.upload(f));return true;},
            dragover:event=>{if(event.dataTransfer?.types.some(type=>type==='Files'||type==='application/x-solo-design-doc-media'))event.preventDefault();},
        }),

    ]})});
    const themeObserver=new MutationObserver(()=>{const user=themeUser();if(user.color!==provider.options.user.color){provider.options.user=user;if(provider.canWrite)provider.awareness.setLocalStateField('user',user);}});
    themeObserver.observe(document.head,{childList:true,subtree:true,characterData:true});
    return {
        connected:value=>provider.connected(value),
        permissions(value){images.canWrite=value;toolbar.hidden=!value;provider.options.canWrite=value;provider.connected(provider.online);},
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
        imagesChanged:()=>images.refresh(),
        refreshSketches:()=>images.refresh(),
        dispose(){themeObserver.disconnect();images.dispose();view.destroy();provider.dispose();root.replaceChildren();},
    };
}
