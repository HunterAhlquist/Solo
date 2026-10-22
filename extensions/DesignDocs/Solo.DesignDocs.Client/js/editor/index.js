import { EditorState, Compartment } from '@codemirror/state';
import { EditorView, keymap } from '@codemirror/view';
import { defaultKeymap } from '@codemirror/commands';
import { markdown } from '@codemirror/lang-markdown';
import { yCollab, yUndoManagerKeymap } from 'y-codemirror.next';
import { Provider } from '../sync/provider.js';
import { livePreview } from './preview.js';
import { headings } from './markdown.js';
import { Images } from '../media/images.js';
export function mountEditor(root, bridge, options) {
    const readOnly = new Compartment(); let view;
    const fallback = document.createElement('pre'), editorRoot = document.createElement('div');
    fallback.className = 'design-docs-stored-markdown'; root.append(fallback,editorRoot);
    const provider = new Provider({send:(kind,data)=>bridge.invokeMethodAsync('Send',kind,data)}, {
        ...options,
        writable:value=>{images.canWrite=value;upload.disabled=!value;view?.dispatch({effects:readOnly.reconfigure([EditorState.readOnly.of(!value),EditorView.editable.of(value)])});},
        status:message=>bridge.invokeMethodAsync('Status',message),
        copy:(text,seq)=>bridge.invokeMethodAsync('Copy',text,seq,headings(text)),
        unseeded:markdown=>{fallback.textContent=markdown;editorRoot.hidden=true;},
    });
    const insert=text=>{if(!provider.canWrite)return;const r=view.state.selection.main;view.dispatch({changes:{from:r.from,to:r.to,insert:text},selection:{anchor:r.from+text.length}});view.focus();};
    const images=new Images(bridge,options.canWrite,insert,message=>bridge.invokeMethodAsync('Status',message));
    const toolbar=document.createElement('div');toolbar.className='design-docs-media-tools';
    const upload=document.createElement('button');upload.type='button';upload.textContent='Add image';upload.onclick=()=>images.choose();toolbar.append(upload);
    root.prepend(toolbar);toolbar.hidden=!options.canWrite;
    view = new EditorView({parent:editorRoot,state:EditorState.create({extensions:[
        markdown(),keymap.of([...defaultKeymap,...yUndoManagerKeymap]),EditorView.lineWrapping,livePreview(bridge,images),
        yCollab(provider.doc.getText('markdown'),provider.awareness),
        readOnly.of([EditorState.readOnly.of(true),EditorView.editable.of(false)]),
        EditorView.domEventHandlers({focus:()=>{bridge.invokeMethodAsync('Focus',true);},blur:()=>{bridge.invokeMethodAsync('Focus',false);},
            paste:event=>{const files=[...(event.clipboardData?.files??[])];if(!provider.canWrite||!files.length)return false;event.preventDefault();files.forEach(f=>images.upload(f));return true;},
            drop:event=>{const files=[...(event.dataTransfer?.files??[])];if(!provider.canWrite||!files.length)return false;event.preventDefault();files.forEach(f=>images.upload(f));return true;},
            dragover:event=>{if(event.dataTransfer?.types.includes('Files'))event.preventDefault();},
        }),
        
    ]})});
    return {
        connected:value=>provider.connected(value),
        permissions(value){images.canWrite=value;toolbar.hidden=!value;provider.options.canWrite=value;provider.connected(provider.online);},
        async receive(kind,data) {
            await provider.receive(kind,data);
            if(kind==='state'||kind==='update'){fallback.textContent='';editorRoot.hidden=false;}
            if(kind==='synced' && options.fragment) {
                const target=decodeURIComponent(options.fragment.replace(/^#/,'')); options.fragment=null;
                const content=provider.doc.getText('markdown').toString(), list=headings(content);
                const index=list.findIndex(h=>h.slug===target);
                if(index<0) bridge.invokeMethodAsync('Status','Heading not found · opened at the top');
                else view.dispatch({effects:EditorView.scrollIntoView(list[index].from,{y:'start'})});
            }
        },
        presence:users=>provider.presence(users),
        insert,
        imagesChanged:()=>images.refresh(),
        dispose(){images.dispose();view.destroy();provider.dispose();root.replaceChildren();},
    };
}
