import {sketchStrokes} from '../sketch/objects.js';
import { unzipSync,zipSync } from 'fflate';
import { parser } from '@lezer/markdown';
import * as Y from 'yjs';
import { decode,encode } from '../sync/protocol.js';
import { normalizePath,resolvePath,imageTargets,mediaType } from '../media/vault-paths.js';
import { pageSize,paint } from '../sketch/render.js';
import { loadFont } from '../sketch/font.js';
const encoder=new TextEncoder(),decoder=new TextDecoder('utf-8',{fatal:true,ignoreBOM:true});
const imageType=path=>/\.(png|jpe?g|webp|gif)$/i.test(path);
const metadata=new Set(['__macosx','.ds_store','.appledouble','.spotlight-v100','.trashes','.fseventsd','.temporaryitems','.obsidian','.git','.svn','.hg','thumbs.db','desktop.ini']);
export function inspectZip(bytes,existing=[]){
    const skipped=[],seen=new Set(),videoFiles=new Set(),missingVideos=new Set();let expanded=0;
    const files=unzipSync(bytes,{filter:file=>{
        if(file.name.endsWith('/'))return false;const path=normalizePath(file.name);
        const omit=reason=>{skipped.push(`${file.name}: ${reason}`);return false;};
        if(!path||file.name.split(/[\\/]/).includes('..'))return omit('invalid path');
        if(path.split('/').some(p=>p.startsWith('._')||metadata.has(p.toLowerCase())))return false;
        if(seen.has(path))throw new Error(`Duplicate zip path: ${path}`);seen.add(path);
        if(mediaType(path)==='video'){videoFiles.add(path);return omit('video skipped; video import is not supported yet');}
        if(/\.(canvas|excalidraw)(\.md)?$/i.test(path))return omit('unsupported canvas or Excalidraw content');
        if(!/\.md$/i.test(path)&&!imageType(path))return omit('not a markdown note or supported raster image');
        if(file.originalSize>(/\.md$/i.test(path)?1048576:25*1024*1024))return omit('file exceeds the import size limit');
        expanded+=file.originalSize;if(expanded>256*1024*1024)throw new Error('Expanded import exceeds 256 MB. Split the vault into smaller zips.');return true;
    }});
    const normalized=new Map(Object.entries(files).map(([path,data])=>[normalizePath(path),data])),notes=[],wanted=new Set();
    for(const [path,data]of normalized)if(/\.md$/i.test(path)){
        const parts=path.split('/'),names=parts.slice(0,-1).concat(parts.at(-1).slice(0,-3));
        if(names.some(n=>!n.trim()||n!==n.trim()||n.length>200||/[\x00-\x1f\\/:#\[\]|]/.test(n)||n==='.'||n==='..')){skipped.push(`${path}: unsupported file or folder name`);continue;}
        let markdown;try{markdown=decoder.decode(data);}catch{skipped.push(`${path}: note is not UTF-8`);continue;}
        notes.push({path,markdown});
        for(const target of imageTargets(markdown)){
            const kind=mediaType(target);if(kind==='sketch')continue;
            if(kind==='video'){
                if(!resolvePath([...videoFiles],target,path))missingVideos.add(`${path}: video embed skipped (${target})`);
                continue;
            }
            if(kind!=='image'){skipped.push(`${path}: unsupported attachment (${target})`);continue;}
            const resolved=resolvePath([...normalized.keys()].filter(imageType),target,path);
            if(resolved)wanted.add(resolved);else skipped.push(`${path}: image missing or ambiguous (${target})`);
        }
    }
    const images=[...wanted].map(path=>({path,bytes:normalized.get(path)}));
    for(const path of normalized.keys())if(imageType(path)&&!wanted.has(path))skipped.push(`${path}: not embedded by an imported note`);
    const conflicts=notes.map(n=>n.path).concat(images.map(i=>i.path)).filter(p=>existing.includes(p));
    skipped.push(...missingVideos);return {notes,images,skipped,conflicts,videos:videoFiles.size+missingVideos.size};
}
export function batches(notes){const result=[];let current=[];for(const note of notes){const next=current.concat(note);if(current.length&&(next.length>100||encoder.encode(JSON.stringify({notes:next})).length>5*1024*1024)){result.push(current);current=[];}current.push(note);}if(current.length)result.push(current);return result;}
export function markdownFromState(state){if(!state.updates.length)return state.seedMarkdown??'';const doc=new Y.Doc();try{for(const update of state.updates)Y.applyUpdate(doc,typeof update.data==='string'?decode(update.data):update.data);return doc.getText('markdown').toString();}finally{doc.destroy();}}
export function exportSketch(markdown,sketch,notePath=''){
    const path=sketch.path+'.png',tree=parser.parse(markdown);
    markdown=markdown.replace(/!\[\[([^\]]+)\]\]|!\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)/g,(raw,wiki,angled,plain,offset)=>{
        for(let node=tree.resolveInner(offset,1);node;node=node.parent)if(/^(FencedCode|CodeBlock|InlineCode)$/.test(node.name))return raw;
        const target=(wiki??angled??plain).split('|')[0];let decoded;try{decoded=decodeURIComponent(target);}catch{return raw;}
        return decoded===sketch.path||decoded===sketch.name+'.sketch'?raw.replace(target,wiki?path:'../'.repeat(Math.max(0,notePath.split('/').length-1))+path):raw;
    });
    return {path,markdown};
}
async function originalImage(image){
    const type=/\.png$/i.test(image.path)?'image/png':/\.jpe?g$/i.test(image.path)?'image/jpeg':/\.gif$/i.test(image.path)?'image/gif':'image/webp';
    const source=URL.createObjectURL(new Blob([image.bytes],{type}));
    try{const picture=new Image();picture.src=source;await picture.decode();
        if(Math.max(picture.naturalWidth,picture.naturalHeight)<=2560&&image.bytes.length<=10*1024*1024)return {...image,type};
        if(type==='image/gif')throw new Error('Oversized GIF cannot be resized while preserving its animation.');
        const scale=Math.min(1,2560/Math.max(picture.naturalWidth,picture.naturalHeight)),canvas=document.createElement('canvas');canvas.width=Math.max(1,Math.round(picture.naturalWidth*scale));canvas.height=Math.max(1,Math.round(picture.naturalHeight*scale));canvas.getContext('2d').drawImage(picture,0,0,canvas.width,canvas.height);
        const blob=await new Promise(resolve=>canvas.toBlob(resolve,type,.88));if(!blob||blob.size>10*1024*1024)throw new Error('Converted image exceeds 10 MB.');return {path:image.path,type,bytes:new Uint8Array(await blob.arrayBuffer())};
    }finally{URL.revokeObjectURL(source);}
}
export async function chooseImport(existing){return await new Promise((resolve,reject)=>{
    const input=document.createElement('input');input.type='file';input.accept='.zip,application/zip';input.hidden=true;document.body.append(input);
    input.addEventListener('cancel',()=>{input.remove();resolve(null);},{once:true});input.addEventListener('change',async()=>{const file=input.files[0];input.remove();if(!file){resolve(null);return;}
        try{if(file.size>100*1024*1024)throw new Error('The zip exceeds 100 MB. Split it into smaller zips.');const plan=inspectZip(new Uint8Array(await file.arrayBuffer()),existing);
            const images=[];for(const image of plan.images){try{images.push(await originalImage(image));}catch(e){plan.skipped.push(`${image.path}: ${e.message}`);}}plan.images=images;plan.conflicts=plan.notes.map(n=>n.path).concat(images.map(i=>i.path)).filter(path=>existing.includes(path));
            resolve({plan,summary:{notes:plan.notes.length,images:images.length,skipped:plan.skipped,conflicts:plan.conflicts,videos:plan.videos}});
        }catch(e){reject(e);}
    },{once:true});input.click();
});}
export function createTransfer(bridge,options){let plan,disposed=false;const urls=[];
    return {
        async inspect(existing){const selected=await chooseImport(existing);plan=selected?.plan;return selected?.summary??null;},
        async import(){if(!plan)return;let done=0,total=plan.notes.length+plan.images.length;
            for(const notes of batches(plan.notes)){if(disposed)return;await bridge.invokeMethodAsync('Notes',JSON.stringify({notes}));done+=notes.length;await bridge.invokeMethodAsync('Progress',`Imported ${done} of ${total} files`);}
            for(const image of plan.images){if(disposed)return;await bridge.invokeMethodAsync('Image',image.path,image.type,encode(image.bytes));done++;await bridge.invokeMethodAsync('Progress',`Imported ${done} of ${total} files`);await new Promise(r=>setTimeout(r,125));}
            plan=null;
        },
        async export(manifest){const files=Object.create(null);let bytes=0;const font=await loadFont();
            for(const note of manifest.notes){if(disposed)return;const state=await bridge.invokeMethodAsync('State',note.id);let markdown=markdownFromState(state);
                for(const sketch of manifest.sketches.filter(s=>s.noteId===note.id)){
                    const state=await bridge.invokeMethodAsync('State',sketch.id),doc=new Y.Doc();try{for(const update of state.updates)Y.applyUpdate(doc,decode(update.data));
                        const meta={pageSize:sketch.pageSize,orientation:sketch.orientation,pattern:sketch.pattern,...doc.getMap('meta').toJSON()},canvas=document.createElement('canvas');pageSize(meta);paint(canvas.getContext('2d'),sketchStrokes(doc),[...doc.getMap('texts')].map(([id,text])=>({id,...text.toJSON()})),meta,{font,frame:0,exporting:true});
                        const blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/png')),data=new Uint8Array(await blob.arrayBuffer());const exported=exportSketch(markdown,sketch,note.path);files[exported.path]=data;bytes+=data.length;markdown=exported.markdown;
                    }finally{doc.destroy();}}
                const content=encoder.encode(markdown);files[note.path]=content;bytes+=content.length;await bridge.invokeMethodAsync('Progress',`Exporting ${note.path}`);
            }
            for(const image of manifest.images){if(disposed)return;const data=decode(await bridge.invokeMethodAsync('ImageBytes',image.id));files[image.path]=data;bytes+=data.length;if(bytes>512*1024*1024)throw new Error('Export exceeds the 512 MB browser memory limit.');}
            if(bytes>512*1024*1024)throw new Error('Export exceeds the 512 MB browser memory limit.');
            const zipped=zipSync(files,{level:6}),url=URL.createObjectURL(new Blob([zipped],{type:'application/zip'}));urls.push(url);const link=document.createElement('a');link.href=url;link.download=options.name+'.zip';document.body.append(link);link.click();link.remove();
        },
        dispose(){disposed=true;plan=null;urls.forEach(url=>URL.revokeObjectURL(url));},
    };
}
