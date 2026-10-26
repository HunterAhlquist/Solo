import { refreshSketchPreview } from './sketch-preview.js';
import { decode } from '../sync/protocol.js';
import { attachmentName } from './vault-paths.js';
export async function prepareImage(file) {
    if (file.type === 'image/svg+xml' || /\.svg$/i.test(file.name ?? '')) throw new Error('SVG images are not supported.');
    const source = URL.createObjectURL(file);
    try {
        const image = new Image(); image.src = source; await image.decode();
        const scale = Math.min(1, 2560 / Math.max(image.naturalWidth, image.naturalHeight));
        const canvas = document.createElement('canvas'); canvas.width = Math.max(1,Math.round(image.naturalWidth*scale));canvas.height=Math.max(1,Math.round(image.naturalHeight*scale));
        canvas.getContext('2d').drawImage(image,0,0,canvas.width,canvas.height);
        const blob = await new Promise(resolve=>canvas.toBlob(resolve,'image/webp',.88));
        const output = blob?.type==='image/webp' ? blob : await new Promise(resolve=>canvas.toBlob(resolve,'image/jpeg',.9));
        if(!output || output.size>10*1024*1024)throw new Error('The image is too large to upload.');
        return {bytes:new Uint8Array(await output.arrayBuffer()),type:output.type,extension:output.type==='image/webp'?'webp':'jpg'};
    } finally { URL.revokeObjectURL(source); }
}
export function imageAngle(path) {let seed=0;for(const c of path)seed=(seed*31+c.charCodeAt(0))|0;return (Math.abs(seed)%701)/100-3.5;}
export class Images {
    constructor(bridge,canWrite,insert,status,actions={}) {
        Object.assign(this,{bridge,canWrite,insert,status,actions});
        this.revisions=new Map();this.cache=new Map();this.cards=new Set();this.inputs=new Set();this.disposed=false;this.dragged=null;
    }
    get canWrite(){return this._canWrite;}
    set canWrite(value){this._canWrite=value;for(const entry of this.cards??[]){entry.button.draggable=value;for(const image of entry.button.querySelectorAll('img'))image.draggable=value;}
        const remove=this.dialog?.querySelector('[aria-label="Remove attachment from note"]');if(remove)remove.hidden=!value;
        const replace=this.dialog?.querySelector('[aria-label="Replace image"]');if(replace)replace.hidden=!value||!!this.viewerSketch;
    }
    async remove(reference) {
        if(!this.canWrite||this.disposed)return false;
        if(!await this.bridge.invokeMethodAsync('ConfirmRemoveAttachment',reference.path,!!reference.sketch))return false;
        return this.canWrite&&!this.disposed&&!!this.actions.remove?.(reference);
    }
    async resolve(path) {
        if(!this.cache.has(path)){const loading=(async()=>{
            let blob;
            if(/\.sketch$/i.test(path)){
                const canvas=document.createElement('canvas');canvas.dataset.sketch=path.slice(0,-7);
                await refreshSketchPreview(canvas,this.bridge);
                blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/png'));
                if(!blob)throw new Error('Sketch preview could not be rendered.');
            }else{
                const data=await this.bridge.invokeMethodAsync('Image',path);
                blob=new Blob([decode(data.base64)],{type:data.contentType});
            }
            const url=URL.createObjectURL(blob);
            if(this.disposed){URL.revokeObjectURL(url);throw new Error('Page closed');}
            return url;
        })().catch(e=>{if(this.cache.get(path)===loading)this.cache.delete(path);throw e;});this.cache.set(path,loading);}
        return this.cache.get(path);
    }
    card(entries) {
        const button=document.createElement('button');button.type='button';
        button.className=entries.length>1?'design-docs-image-stack':'design-docs-image-card';
        button.setAttribute('aria-label',entries.length>1?`Open ${entries.length} attachments`:`Open ${attachmentName(entries[0].path)}`);
        button.draggable=this.canWrite;
        button.style.width=`calc(${148+Math.min(entries.length-1,7)*24}px * var(--design-docs-scale, 1))`;
        const references=entries.map(e=>this.actions.reference?.(e)??e),entry={button,entries};this.cards.add(entry);
        for(const [index,item] of entries.entries()){
            const img=document.createElement('img');img.alt=attachmentName(item.path);img.draggable=this.canWrite;img.dataset.mediaIndex=String(index);
            img.style.transform=`translate(calc(${Math.min(index,7)*24}px * var(--design-docs-scale, 1)),calc(${index%2*6}px * var(--design-docs-scale, 1))) rotate(${imageAngle(item.path)*(entries.length>1?2:1)}deg)`;img.style.zIndex=index;
            this.thumbnail(img,item.path);button.append(img);
        }
        if(entries.length>1){const count=document.createElement('span');count.className='design-docs-stack-count';count.textContent=String(entries.length);button.append(count);}
        let front=entries.length-1;
        button.addEventListener('click',event=>{
            const clicked=event.target.closest('img[data-media-index]');
            this.open(references,selected=>{
                front=selected;[...button.querySelectorAll('img')].forEach((img,i)=>img.style.zIndex=i===selected?entries.length+1:i);
            },clicked?Number(clicked.dataset.mediaIndex):front,selected=>button.querySelector(`img[data-media-index="${selected}"]`));
        });
        button.addEventListener('dragstart',event=>{
            if(!this.canWrite){event.preventDefault();return;}
            const item=event.target.closest('img[data-media-index]');
            this.dragged=item?references[Number(item.dataset.mediaIndex)]:this.actions.reference?.({from:entries[0].from,to:entries.at(-1).to,raw:this.actions.text?.(entries[0].from,entries.at(-1).to)??entries.map(e=>e.raw).join(' ')})??null;
            event.dataTransfer.setData('application/x-solo-design-doc-media','move');event.dataTransfer.effectAllowed='move';
        });
        button.addEventListener('dragend',()=>{this.dragged=null;});
        button.addEventListener('contextmenu',async event=>{
            if(!this.canWrite||entries[0].sketch)return;event.preventDefault();
            if(await this.bridge.invokeMethodAsync('ImageMenu',entries[0].path,event.clientX,event.clientY))this.choose(entries[0].path);
        });
        return button;
    }
    release(button){for(const entry of this.cards)if(entry.button===button)this.cards.delete(entry);}
    async open(references,onSelection=()=>{},initialIndex=0,sourceFor=()=>null) {
        this.closeViewer?.();
        const dialog=document.createElement('dialog');dialog.className='design-docs-image-viewer';
        dialog.setAttribute('aria-label','Attachments');this.dialog=dialog;
        let index=initialIndex,request=0,closing=false,closed=false,presented=false,flight,hiddenSource,closingTask;const items=[...references];
        const duration=matchMedia('(prefers-reduced-motion: reduce)').matches?0:260;
        const restoreSource=()=>{if(hiddenSource){hiddenSource.style.visibility='';hiddenSource=null;}};
        const sourceTransform=()=>{
            const source=sourceFor(references.indexOf(items[index]));
            if(!source?.isConnected||!image.naturalWidth)return null;
            const from=source.getBoundingClientRect(),to=image.getBoundingClientRect(),style=getComputedStyle(source);
            if(!to.width||!to.height||!from.width||!from.height)return null;
            const width=source.clientWidth-parseFloat(style.paddingLeft)-parseFloat(style.paddingRight);
            const height=source.clientHeight-parseFloat(style.paddingTop)-parseFloat(style.paddingBottom);
            const scale=Math.min(width/image.naturalWidth,height/image.naturalHeight)*image.naturalWidth/to.width;
            const matrix=new DOMMatrixReadOnly(style.transform==='none'?undefined:style.transform);
            const angle=Math.atan2(matrix.b,matrix.a)*180/Math.PI;
            hiddenSource=source;
            return `translate(${from.x+from.width/2-to.x-to.width/2}px,${from.y+from.height/2-to.y-to.height/2}px) rotate(${angle}deg) scale(${scale})`;
        };
        const cleanup=()=>{
            if(closed)return;closed=true;++request;flight?.cancel();restoreSource();dialog.remove();
            if(this.dialog===dialog){this.dialog=null;this.refreshViewer=null;this.closeViewer=null;}
        };
        const finish=()=>{if(dialog.open)dialog.close();cleanup();};
        const dismiss=()=>closingTask??=(async()=>{
            if(closed)return;closing=true;++request;dialog.dataset.phase='closing';
            const current=getComputedStyle(image).transform;flight?.cancel();
            const transform=presented?sourceTransform():null;
            if(hiddenSource)hiddenSource.style.visibility='hidden';
            dialog.classList.remove('is-presented');
            flight=image.animate(transform?[{transform:current,opacity:1},{transform,opacity:1}]:[{opacity:1},{opacity:0}],{duration,easing:'cubic-bezier(.4,0,.2,1)',fill:'both'});
            await Promise.allSettled([flight.finished,new Promise(resolve=>setTimeout(resolve,duration))]);finish();
        })();
        this.closeViewer=finish;
        const header=document.createElement('header'),label=document.createElement('span'),counter=document.createElement('span');
        const stage=document.createElement('div');stage.className='design-docs-viewer-stage';
        const image=document.createElement('img');image.draggable=false;
        const caption=document.createElement('p');caption.className='design-docs-viewer-error';caption.hidden=true;
        const footer=document.createElement('footer');
        const control=(icon,name,action)=>{const b=document.createElement('button');b.type='button';b.className='mud-button-root mud-icon-button mud-icon-button-size-medium mud-ripple';b.setAttribute('aria-label',name);b.title=name;b.onclick=action;
            const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox','0 0 24 24');svg.setAttribute('aria-hidden','true');svg.classList.add('mud-icon-root','mud-svg-icon');
            svg.innerHTML=this.actions.icons?.[icon]??'';b.append(svg);return b;};
        const close=control('close','Close viewer',()=>dismiss());close.classList.add('design-docs-viewer-close');
        const previous=control('previous','Previous attachment',()=>show(index-1));previous.classList.add('design-docs-viewer-previous');
        const next=control('next','Next attachment',()=>show(index+1));next.classList.add('design-docs-viewer-next');
        const edit=control('edit','Edit sketch',async()=>{
            const path=items[index].path;
            await dismiss();if(this.disposed)return;await this.bridge.invokeMethodAsync('OpenSketch',path.slice(0,-7));
        });
        let confirming=false;
        const remove=control('remove','Remove attachment from note',async()=>{
            if(!this.canWrite||confirming||closing||closed)return;
            const item=items[index];confirming=true;remove.disabled=true;let removed=false;
            // A native modal would cover MudBlazor's confirmation dialog.
            dialog.close();dialog.show();
            try{removed=await this.remove(item);}catch(e){if(!this.disposed)this.status(e.message);}
            finally{
                confirming=false;remove.disabled=false;
                if(!closed&&!closing){dialog.close();dialog.showModal();remove.focus();}
            }
            if(!removed||closing||closed)return;
            items.splice(items.indexOf(item),1);if(!items.length){dismiss();return;}show(Math.min(index,items.length-1));
        });
        remove.classList.add('design-docs-viewer-remove');
        const download=control('download','Download attachment',async()=>{
            try{const item=items[index],a=document.createElement('a');a.href=await this.resolve(item.path);a.download=item.sketch?attachmentName(item.path).replace(/\.sketch$/i,'.png'):attachmentName(item.path);document.body.append(a);a.click();a.remove();}catch(e){this.status(e.message);}
        });
        const replace=control('replace','Replace image',()=>this.choose(items[index].path));
        this.refreshViewer=matches=>{if(matches(items[index].path))return show(index);};
        const show=async position=>{
            if(closing||closed)return;
            if(presented){flight?.cancel();restoreSource();dialog.dataset.phase='open';}
            index=(position+items.length)%items.length;const item=items[index],serial=++request;this.viewerSketch=item.sketch;onSelection(references.indexOf(item));
            label.textContent=attachmentName(item.path);counter.textContent=`${index+1} / ${items.length}`;
            image.alt=attachmentName(item.path);image.style.visibility='hidden';caption.hidden=true;
            previous.hidden=next.hidden=items.length<2;edit.hidden=!item.sketch;remove.hidden=!this.canWrite;replace.hidden=!this.canWrite||item.sketch;download.setAttribute('aria-label',item.sketch?'Export sketch PNG':'Download image');download.title=download.getAttribute('aria-label');
            try{
                const url=await this.resolve(item.path);if(serial!==request||!dialog.open||closing)return;
                image.src=url;await image.decode();if(serial!==request||!dialog.open||closing)return;
                if(!presented){
                    // Raise the selected preview before the background starts to fade.
                    const transform=sourceTransform();await new Promise(requestAnimationFrame);
                    if(closed||closing)return;
                    presented=true;dialog.dataset.phase='opening';dialog.classList.add('is-presented');
                    if(hiddenSource)hiddenSource.style.visibility='hidden';
                    const opening=image.animate(transform?[{transform,opacity:1},{transform:'none',opacity:1}]:[{opacity:0},{opacity:1}],{duration,easing:'cubic-bezier(.2,0,0,1)',fill:'both'});flight=opening;image.style.visibility='';
                    await opening.finished.catch(()=>{});
                    if(!closing&&!closed&&flight===opening){restoreSource();opening.cancel();dialog.dataset.phase='open';}
                }else image.style.visibility='';
            }catch(e){if(serial===request&&!closed){caption.textContent=e.message;caption.hidden=false;dialog.classList.add('is-presented');}}
        };
        header.append(label,counter,close);stage.append(previous,image,caption,next);footer.append(download,replace,edit,remove);dialog.append(header,stage,footer);
        dialog.addEventListener('click',event=>{if(event.target===dialog||event.target===stage)dismiss();});
        dialog.addEventListener('cancel',event=>{event.preventDefault();dismiss();});
        dialog.addEventListener('keydown',event=>{
            if(event.key==='ArrowLeft'||event.key==='ArrowRight'){
                event.preventDefault();event.stopPropagation();show(index+(event.key==='ArrowLeft'?-1:1));
            }
        });
        let start;image.addEventListener('pointerdown',event=>{start=event.clientX;image.setPointerCapture(event.pointerId);});
        image.addEventListener('pointerup',event=>{if(start!==undefined&&Math.abs(event.clientX-start)>40)show(index+(event.clientX<start?1:-1));start=undefined;});
        image.addEventListener('pointercancel',()=>{start=undefined;});
        dialog.addEventListener('close',()=>{if(!dialog.open)cleanup();});
        document.body.append(dialog);dialog.showModal();close.focus();await show(initialIndex);
    }
    choose(replace=null) {
        const input=document.createElement('input');input.type='file';input.accept='image/*';input.hidden=true;document.body.append(input);this.inputs.add(input);
        input.addEventListener('change',()=>{if(input.files[0])this.upload(input.files[0],replace);input.remove();this.inputs.delete(input);},{once:true});
        input.addEventListener('cancel',()=>{input.remove();this.inputs.delete(input);},{once:true});input.click();
    }
    async upload(file,replace=null) {
        if(!this.canWrite||this.disposed)return;
        try{
            const image=await prepareImage(file);if(this.disposed)return;
            const name=(file.name??'image').replace(/\.[^.]+$/,'').replace(/[^\p{L}\p{N}_-]/gu,'-').slice(0,80)||'image';
            const path=replace??`${name}.${image.extension}`;
            const stored=await this.bridge.invokeMethodAsync('UploadImage',image.bytes,image.type,path,!!replace);
            if(this.disposed)return;
            if(!replace)this.insert(`![[${stored}]]`);else await this.refresh(item=>item===path);
        }catch(e){if(!this.disposed)this.status(e.message);}
    }
    thumbnail(image,path){
        const revision=this.revisions.get(path);
        this.resolve(path).then(url=>{if(!this.disposed&&this.revisions.get(path)===revision)image.src=url;})
            .catch(()=>{if(!this.disposed&&this.revisions.get(path)===revision)image.alt=`Unavailable: ${attachmentName(path)}`;});
    }
    async refresh(matches=()=>true){
        const paths=new Set([...this.cache.keys(),...[...this.cards].flatMap(card=>card.entries.map(item=>item.path))]);
        for(const path of paths)if(matches(path))this.revisions.set(path,(this.revisions.get(path)??0)+1);
        for(const [path,promise] of this.cache)if(matches(path)){this.cache.delete(path);promise.then(url=>URL.revokeObjectURL(url)).catch(()=>{});}
        if(this.dialog?.open)this.refreshViewer?.(matches);
        for(const entry of [...this.cards]){
            if(!entry.button.isConnected){this.cards.delete(entry);continue;}
            const images=entry.button.querySelectorAll('img');entry.entries.forEach((item,i)=>{if(matches(item.path))this.thumbnail(images[i],item.path);});
        }
    }
    dispose(){this.disposed=true;for(const input of this.inputs)input.remove();this.inputs.clear();this.closeViewer?.();for(const promise of this.cache.values())promise.then(url=>URL.revokeObjectURL(url)).catch(()=>{});this.cache.clear();this.cards.clear();this.revisions.clear();}
}
