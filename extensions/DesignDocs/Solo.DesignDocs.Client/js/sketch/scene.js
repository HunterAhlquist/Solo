import {pageSize,rasterize,applyStrokes,paint,paintTexts,paintPixels,paintPaper,splitSelection,footprint,paintStroke} from './render.js';

// The three seeded boil frames are immutable until the shared strokes change.
// Pointer feedback copies a bitmap; it never replays the committed drawing.
export class SketchScene {
    constructor(createCanvas){this.createCanvas=createCanvas;this.style={};this.held=new Map();this.setScene([],{});}
    setScene(ops,meta){
        const [width,height]=pageSize(meta),previous=this.ops;
        // Collaborators mostly append strokes. Painting only the new ones over each cached
        // frame gives the same pixels as a replay, at a cost that doesn't grow with the page.
        const extended=this.frames?.size>0&&width===this.width&&height===this.height&&meta?.pattern===this.meta?.pattern
            &&ops.length>=previous.length&&previous.every((stroke,index)=>ops[index]===stroke);
        if(!extended)this.frames=new Map();
        else if(ops.length>previous.length){
            const added=ops.slice(previous.length),region=footprint(added);
            for(const [number,value] of this.frames){
                applyStrokes(value.raster,added,width,height,number,previous.length);
                if(value.canvas)this.repaint(value.canvas,value.raster,region);
                // An eraser held over the old page starts again from the new one.
                value.erase=null;
            }
        }
        this.ops=ops;this.meta=meta;this.width=width;this.height=height;this.drag=null;
    }
    setStyle(style){this.style=style;this.held.clear();for(const value of this.frames.values()){value.canvas=null;value.erase=null;}if(this.drag)this.drag.frames.clear();}
    frame(number){
        let value=this.frames.get(number);
        if(!value){value={raster:rasterize(this.ops,this.width,this.height,number)};this.frames.set(number,value);}
        if(!value.canvas)value.canvas=this.layer(value.raster,true);
        return value;
    }
    hitRaster(){let value=this.frames.get(0);if(!value){value={raster:rasterize(this.ops,this.width,this.height,0)};this.frames.set(0,value);}return value.raster;}
    layer(raster){
        const canvas=this.createCanvas(),context=canvas.getContext('2d');canvas.width=this.width;canvas.height=this.height;
        context.clearRect(0,0,this.width,this.height);context.imageSmoothingEnabled=false;context.drawImage(this.paper(),0,0);
        paintPixels(context,raster,this.width,this.height,this.style.themeInk);return canvas;
    }
    // The page's paper and ruling, drawn once and copied wherever ink is repainted.
    paper(){
        const key=[this.width,this.height,this.meta?.pattern,this.style.paperColor].join('|');
        if(this.paperKey!==key){
            const canvas=this.paperCanvas??=this.createCanvas(),context=canvas.getContext('2d');canvas.width=this.width;canvas.height=this.height;
            context.clearRect(0,0,this.width,this.height);paintPaper(context,this.meta??{},this.style.paperColor);this.paperKey=key;
        }
        return this.paperCanvas;
    }
    // Repaints one rectangle of a page layer to the pixels a full paint gives it.
    repaint(canvas,raster,region){
        const {width,height}=this,left=Math.max(0,region.left),top=Math.max(0,region.top),right=Math.min(width,region.right),bottom=Math.min(height,region.bottom);
        if(right<=left||bottom<=top)return;
        const context=canvas.getContext('2d');context.imageSmoothingEnabled=false;context.clearRect(left,top,right-left,bottom-top);
        context.drawImage(this.paper(),left,top,right-left,bottom-top,left,top,right-left,bottom-top);
        paintPixels(context,raster,width,height,this.style.themeInk,{left,top,right,bottom});
    }
    // A held eraser erases a copy of the committed frame, extended by its new samples each
    // frame. Copying, replaying and repainting the whole page every frame instead grew with
    // the eraser's length and the page's ink, for everyone watching it. Held pen strokes draw
    // over the copy, so a preview erases saved ink only; the saved erase follows stroke order.
    erased(value,erasers,frame){
        let erase=value.erase;
        const current=state=>{const stroke=erasers.find(s=>s.id===state.id),points=stroke?.points;if(!stroke||state.points!==points||state.pointerType!==stroke.pointerType||state.next>points.length)return false;
            const last=state.next?points[state.next-1]:null;return !state.next||(last?.[0]===state.last[0]&&last?.[1]===state.last[1]&&last?.[2]===state.last[2]);};
        if(!erase||![...erase.strokes.values()].every(current)){
            // The collaborator feed rewrote a tail, or an eraser lifted without its stroke
            // saved yet: start over from the committed frame.
            const canvas=value.eraseCanvas??=this.createCanvas(),context=canvas.getContext('2d');canvas.width=this.width;canvas.height=this.height;
            context.clearRect(0,0,this.width,this.height);context.drawImage(value.canvas,0,0);
            erase=value.erase={raster:{pixels:value.raster.pixels.slice()},canvas,strokes:new Map()};
        }
        for(const stroke of erasers){
            const points=Array.isArray(stroke.points)?stroke.points:[];
            let state=erase.strokes.get(stroke.id);if(!state){state={id:stroke.id,points,pointerType:stroke.pointerType,next:0,last:null};erase.strokes.set(stroke.id,state);}
            if(points.length<=state.next)continue;
            const region=footprint([{points:points.slice(Math.max(0,state.next-1))}]);
            paintStroke(erase.raster,stroke,this.width,this.height,frame,0,state);
            this.repaint(erase.canvas,erase.raster,region);state.last=points.at(-1).slice(0,3);
        }
        return erase.canvas;
    }
    dragFrame(selection,number){
        if(this.drag?.selection!==selection){this.drag={selection,parts:splitSelection(this.ops,selection.region,this.width,this.height,0,0,selection.ids),frames:new Map()};}
        let value=this.drag.frames.get(number);
        if(!value){const {stationary,moved}=this.drag.parts;
            const base=rasterize(stationary,this.width,this.height,number);
            // Retain the brush footprint just outside the page while translating a cut.
            const padding=16,width=this.width+padding*2,height=this.height+padding*2;
            const moving=rasterize(moved.map(stroke=>({...stroke,points:stroke.points.map(([x,y,p])=>[x+padding,y+padding,p])})),width,height,number);
            const overlay=this.createCanvas(),context=overlay.getContext('2d');
            overlay.width=width;overlay.height=height;
            paintPixels(context,moving,width,height,this.style.themeInk);
            value={base,moving,padding,width,height,canvas:this.layer(base),overlay};this.drag.frames.set(number,value);
        }
        return value;
    }
    // Held strokes grow by a few samples a frame. Each keeps its pixels and a bitmap per
    // boil frame, covering only its own footprint, and extends them, rather than replaying
    // every sample of every held stroke each frame; that replay grew with stroke length and
    // churned the garbage collector enough to stall the page while people drew.
    heldLayer(stroke,frame){
        const {width,height}=this,points=Array.isArray(stroke.points)?stroke.points:[];
        let held=this.held.get(stroke.id);
        if(!held||held.points!==points||held.pointerType!==stroke.pointerType){held={points,pointerType:stroke.pointerType,frames:new Map()};this.held.set(stroke.id,held);}
        let layer=held.frames.get(frame);
        // The collaborator feed rewrites its tail on overlap and after a gap.
        const last=layer?.next?points[layer.next-1]:null;
        if(!layer||layer.next>points.length||(layer.next&&(last?.[0]!==layer.last[0]||last?.[1]!==layer.last[1]||last?.[2]!==layer.last[2]))){
            layer={canvas:this.createCanvas(),raster:{pixels:new Uint8Array(0),left:0,top:0,width:0,height:0},next:0,last:null};held.frames.set(frame,layer);
        }
        if(points.length>layer.next){
            const found=footprint([{points:points.slice(Math.max(0,layer.next-1))}]),region={left:Math.max(0,found.left),top:Math.max(0,found.top),right:Math.min(width,found.right),bottom:Math.min(height,found.bottom)};
            this.cover(layer,region);
            paintStroke(layer.raster,stroke,width,height,frame,0,layer);
            const {raster}=layer;
            paintPixels(layer.canvas.getContext('2d'),raster,raster.width,raster.height,this.style.themeInk,{left:region.left-raster.left,top:region.top-raster.top,right:region.right-raster.left,bottom:region.bottom-raster.top});
            layer.last=points.at(-1).slice(0,3);
        }
        return layer;
    }
    // Grows a held layer to include a region, with headroom so growth is occasional.
    cover(layer,region){
        const old=layer.raster;if(region.right<=region.left||region.bottom<=region.top)return;
        if(old.width&&region.left>=old.left&&region.top>=old.top&&region.right<=old.left+old.width&&region.bottom<=old.top+old.height)return;
        let left=region.left,top=region.top,right=region.right,bottom=region.bottom;
        if(old.width){
            left=Math.min(left,old.left);top=Math.min(top,old.top);right=Math.max(right,old.left+old.width);bottom=Math.max(bottom,old.top+old.height);
            const padX=Math.ceil((right-left)/4),padY=Math.ceil((bottom-top)/4);
            left=Math.max(0,left-padX);top=Math.max(0,top-padY);right=Math.min(this.width,right+padX);bottom=Math.min(this.height,bottom+padY);
        }
        const raster={pixels:new Uint8Array((right-left)*(bottom-top)),left,top,width:right-left,height:bottom-top};
        for(let y=0;y<old.height;y++)raster.pixels.set(old.pixels.subarray(y*old.width,(y+1)*old.width),(old.top+y-top)*raster.width+old.left-left);
        layer.raster=raster;layer.canvas.width=raster.width;layer.canvas.height=raster.height;
        paintPixels(layer.canvas.getContext('2d'),raster,raster.width,raster.height,this.style.themeInk);
    }
    paintExtra(context,extra,frame){
        const seen=new Set();
        for(const stroke of extra){seen.add(stroke.id);const layer=this.heldLayer(stroke,frame);if(layer.raster.width)context.drawImage(layer.canvas,layer.raster.left,layer.raster.top);}
        for(const id of this.held.keys())if(!seen.has(id))this.held.delete(id);
    }
    render(context,{frame=0,texts=[],extra=[],selection=null,selectionContext=null,font}={}){
        const {width:w,height:h}=this,moving=selection&&(selection.dx||selection.dy),erasing=extra.some(stroke=>stroke.tool==='eraser');
        if(context.canvas.width!==w)context.canvas.width=w;if(context.canvas.height!==h)context.canvas.height=h;
        context.clearRect(0,0,w,h);context.imageSmoothingEnabled=false;
        let raster;
        if(moving){
            const value=this.dragFrame(selection,frame);
            if(!erasing){context.drawImage(value.canvas,0,0);if(!selectionContext)context.drawImage(value.overlay,selection.dx-value.padding,selection.dy-value.padding);}
            else{
                raster={pixels:value.base.pixels.slice()};
                if(!selectionContext)for(let y=0;y<value.height;y++){const targetY=y+selection.dy-value.padding;if(targetY<0||targetY>=h)continue;
                    for(let x=0;x<value.width;x++){const targetX=x+selection.dx-value.padding,ink=value.moving.pixels[y*value.width+x];if(ink&&targetX>=0&&targetX<w)raster.pixels[targetY*w+targetX]=ink;}}
            }
        }else{
            this.drag=null;const value=this.frame(frame);
            if(erasing)context.drawImage(this.erased(value,extra.filter(stroke=>stroke.tool==='eraser'),frame),0,0);
            else{context.drawImage(value.canvas,0,0);for(const other of this.frames.values())other.erase=null;}
        }
        if(raster){applyStrokes(raster,extra,w,h,frame,this.ops.length);paint(context,[],[],this.meta,{...this.style,raster});}
        else this.paintExtra(context,extra.filter(stroke=>stroke.tool!=='eraser'),frame);
        const boxes=moving?(selectionContext?texts.filter(t=>!selection.textIds.has(t.id)):texts.map(t=>selection.textIds.has(t.id)?{...t,x:t.x+selection.dx,y:t.y+selection.dy}:t)):texts;
        paintTexts(context,boxes,{...this.style,font});
        if(moving&&selectionContext){
            // Position this complete cut layer in the viewport, rather than
            // clipping it to the paper during an uncommitted pointer gesture.
            const value=this.dragFrame(selection,frame),surface=selectionContext.canvas;
            if(surface.width!==value.width)surface.width=value.width;if(surface.height!==value.height)surface.height=value.height;
            selectionContext.clearRect(0,0,value.width,value.height);selectionContext.drawImage(value.overlay,0,0);
            paintTexts(selectionContext,texts.filter(t=>selection.textIds.has(t.id)).map(t=>({...t,x:t.x+value.padding,y:t.y+value.padding})),{...this.style,font});
            return {padding:value.padding};
        }
        return null;
    }
    dispose(){this.frames.clear();this.drag=null;this.ops=[];this.held.clear();this.paperCanvas=null;this.paperKey=null;}
}
