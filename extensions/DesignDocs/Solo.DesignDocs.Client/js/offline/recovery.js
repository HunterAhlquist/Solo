import * as Y from 'yjs';
import {zipSync,strToU8} from 'fflate';
import {encode} from '../sync/protocol.js';
import {projectVault} from './store.js';
import {sketchStrokes} from '../sketch/objects.js';
import {paint} from '../sketch/render.js';
import {loadFont} from '../sketch/font.js';
import {exportSketch} from '../io/vault.js';

export async function recoveryArchive(store,vaultId){
    const vault=await store.get('vaults',vaultId),queue=(await store.all('queue')).filter(r=>r.vaultId===vaultId);
    const documents=(await store.all('documents')).filter(r=>r.vaultId===vaultId),images=(await store.all('images')).filter(r=>r.vaultId===vaultId);
    const base=structuredClone(vault.base);
    if(vault.recovery){for(const group of ['folders','notes'])for(const row of vault.recovery.tree[group])if(!base.tree[group].some(r=>r.id===row.id))base.tree[group].push(row);for(const row of vault.recovery.sketches)if(!base.sketches.some(r=>r.id===row.id))base.sketches.push(row);}
    const source=projectVault({...vault,base},queue),files={};
    const folderPath=id=>{const parts=[],seen=new Set();while(id&&!seen.has(id)){seen.add(id);const folder=source.tree.folders.find(f=>f.id===id);if(!folder)break;parts.unshift(folder.name);id=folder.parentId;}return parts;};
    for(const note of source.tree.notes){
        const doc=new Y.Doc(),state=documents.find(d=>d.id===note.id);if(state)Y.applyUpdate(doc,state.state);
        let path=folderPath(note.folderId).concat(note.name+'.md').join('/');if(files[path])path=path.slice(0,-3)+'-'+note.id+'.md';
        let markdown=state?doc.getText('markdown').toString():note.markdown??'';
        if(typeof document!=='undefined')for(const sketch of source.sketches.filter(s=>s.noteId===note.id))markdown=exportSketch(markdown,sketch,path).markdown;
        files[path]=strToU8(markdown);doc.destroy();
    }
    for(const image of images)files[image.path]=image.data;
    if(typeof document!=='undefined')for(const sketch of source.sketches){
        const state=documents.find(d=>d.id===sketch.id);if(!state)continue;const doc=new Y.Doc();Y.applyUpdate(doc,state.state);
        const canvas=document.createElement('canvas'),style=getComputedStyle(document.body);
        paint(canvas.getContext('2d'),sketchStrokes(doc),[...doc.getMap('texts')].map(([id,text])=>({id,...text.toJSON()})),{...sketch,...doc.getMap('meta').toJSON()},
            {font:await loadFont(),frame:0,exporting:true,themeInk:style.color,paperColor:style.getPropertyValue('--mud-palette-surface')||'white'});
        const blob=await new Promise(resolve=>canvas.toBlob(resolve,'image/png'));files[sketch.path+'.png']=new Uint8Array(await blob.arrayBuffer());doc.destroy();
    }
    files['solo-recovery.json']=strToU8(JSON.stringify({format:'solo-offline-recovery-v1',vault,documents:documents.map(d=>({...d,state:encode(d.state)})),queue:queue.map(r=>({...r,data:r.data?encode(r.data):null,command:r.command?{...r.command,data:r.command.data?encode(r.command.data):null}:null}))}));
    return {name:(source.tree.vault.name||'vault')+'-recovery.zip',bytes:zipSync(files)};
}
