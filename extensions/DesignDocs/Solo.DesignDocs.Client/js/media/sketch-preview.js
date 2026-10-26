import {sketchStrokes} from '../sketch/objects.js';
import * as Y from 'yjs';
import { decode } from '../sync/protocol.js';
import { paint } from '../sketch/render.js';
import { loadFont } from '../sketch/font.js';
export async function refreshSketchPreview(canvas,bridge){
    const data=await bridge.invokeMethodAsync('SketchPreview',canvas.dataset.sketch);
    if(!data)throw new Error('Sketch not found in this note.');
    const doc=new Y.Doc();try{
        for(const update of data.updates)Y.applyUpdate(doc,decode(update));
        const font=await loadFont();
        paint(canvas.getContext('2d'),sketchStrokes(doc),[...doc.getMap('texts')].map(([id,text])=>({id,...text.toJSON()})),
            {pageSize:data.sketch.pageSize,orientation:data.sketch.orientation,pattern:data.sketch.pattern,...doc.getMap('meta').toJSON()},
            {font,frame:0,themeInk:getComputedStyle(document.body).color,paperColor:getComputedStyle(document.body).getPropertyValue('--mud-palette-surface')||'white',exporting:true});
    }finally{doc.destroy();}
}
