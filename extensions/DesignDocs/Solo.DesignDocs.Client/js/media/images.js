import { decode } from '../sync/protocol.js';
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
    constructor(bridge,canWrite,insert,status) {this.bridge=bridge;this.canWrite=canWrite;this.insert=insert;this.status=status;this.cache=new Map();this.cards=new Set();this.inputs=new Set();this.disposed=false;}
    async resolve(path) {
        if(!this.cache.has(path))this.cache.set(path,this.bridge.invokeMethodAsync('Image',path).then(data=>{
            const url=URL.createObjectURL(new Blob([decode(data.base64)],{type:data.contentType}));
            if(this.disposed){URL.revokeObjectURL(url);throw new Error('Page closed');}return url;
        }).catch(e=>{this.cache.delete(path);throw e;}));
        return this.cache.get(path);
    }
    card(paths) {
        const button=document.createElement('button');button.type='button';button.className=paths.length>1?'design-docs-image-stack':'design-docs-image-card';
        button.setAttribute('aria-label',paths.length>1?`Open ${paths.length} images`:`Open ${paths[0]}`);
        const entry={button,paths};this.cards.add(entry);
        for(const [index,path] of paths.entries()) {
            const img=document.createElement('img');img.alt=path;img.style.transform=`rotate(${imageAngle(path)}deg)`;img.style.zIndex=index;
            this.resolve(path).then(url=>{if(!this.disposed)img.src=url;}).catch(()=>{img.alt=`Unavailable image: ${path}`;});button.append(img);
        }
        button.addEventListener('click',()=>this.open(paths));
        button.addEventListener('contextmenu',async event=>{if(!this.canWrite)return;event.preventDefault();
            const replace=await this.bridge.invokeMethodAsync('ImageMenu',paths[0],event.clientX,event.clientY);
            if(replace)this.choose(paths[0]);
        });
        return button;
    }
    async open(paths) {
        this.dialog?.remove();const dialog=document.createElement('dialog');dialog.className='design-docs-image-viewer';this.dialog=dialog;
        const image=document.createElement('img');image.alt=paths[0];let index=0;
        const show=async next=>{index=(next+paths.length)%paths.length;image.alt=paths[index];try{image.src=await this.resolve(paths[index]);}catch{image.removeAttribute('src');}};
        const close=document.createElement('button');close.textContent='Close';close.onclick=()=>dialog.close();
        const previous=document.createElement('button');previous.textContent='Previous';previous.onclick=()=>show(index-1);
        const next=document.createElement('button');next.textContent='Next';next.onclick=()=>show(index+1);
        dialog.append(close,image);if(paths.length>1)dialog.append(previous,next);
        let start;image.addEventListener('pointerdown',e=>{start=e.clientX;});image.addEventListener('pointerup',e=>{if(start!==undefined&&Math.abs(e.clientX-start)>40)show(index+(e.clientX<start?1:-1));start=undefined;});
        dialog.addEventListener('close',()=>dialog.remove());document.body.append(dialog);dialog.showModal();await show(0);
    }
    choose(replace=null) {
        const input=document.createElement('input');input.type='file';input.accept='image/*';
        input.hidden=true;document.body.append(input);this.inputs.add(input);
        input.addEventListener('change',()=>{if(input.files[0])this.upload(input.files[0],replace);input.remove();this.inputs.delete(input);},{once:true});
        input.addEventListener('cancel',()=>{input.remove();this.inputs.delete(input);},{once:true});input.click();
    }
    async upload(file,replace=null) {
        if(!this.canWrite||this.disposed)return;
        try {
            const image=await prepareImage(file);if(this.disposed)return;
            const name=(file.name??'image').replace(/\.[^.]+$/,'').replace(/[^\p{L}\p{N}_-]/gu,'-').slice(0,80)||'image';
            const path=replace??`attachments/${name}-${crypto.randomUUID().slice(0,8)}.${image.extension}`;
            await this.bridge.invokeMethodAsync('UploadImage',image.bytes,image.type,path,!!replace);
            if(this.disposed)return;
            if(!replace)this.insert(`![[${path}]]`);await this.refresh();
        }catch(e){if(!this.disposed)this.status(e.message);}
    }
    async refresh() {
        for(const promise of this.cache.values())promise.then(url=>URL.revokeObjectURL(url)).catch(()=>{});this.cache.clear();
        for(const entry of [...this.cards]){
            if(!entry.button.isConnected){this.cards.delete(entry);continue;}
            const images=entry.button.querySelectorAll('img');entry.paths.forEach((path,i)=>this.resolve(path).then(url=>{if(images[i])images[i].src=url;}).catch(()=>{}));
        }
    }
    dispose() {this.disposed=true;for(const input of this.inputs)input.remove();this.inputs.clear();this.dialog?.remove();for(const promise of this.cache.values())promise.then(url=>URL.revokeObjectURL(url)).catch(()=>{});this.cache.clear();this.cards.clear();}
}
