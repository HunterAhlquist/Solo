import {pageSize,rasterize,applyStrokes,paint,paintTexts,paintPixels,splitSelection} from './render.js';

// The three seeded boil frames are immutable until the shared strokes change.
// Pointer feedback copies a bitmap; it never replays the committed drawing.
export class SketchScene {
    constructor(createCanvas){this.createCanvas=createCanvas;this.setScene([],{});this.setStyle({});}
    setScene(ops,meta){this.ops=ops;this.meta=meta;[this.width,this.height]=pageSize(meta);this.frames=new Map();this.drag=null;}
    setStyle(style){this.style=style;for(const value of this.frames.values())value.canvas=null;if(this.drag)this.drag.frames.clear();}
    frame(number){
        let value=this.frames.get(number);
        if(!value){value={raster:rasterize(this.ops,this.width,this.height,number)};this.frames.set(number,value);}
        if(!value.canvas)value.canvas=this.layer(value.raster,true);
        return value;
    }
    hitRaster(){let value=this.frames.get(0);if(!value){value={raster:rasterize(this.ops,this.width,this.height,0)};this.frames.set(0,value);}return value.raster;}
    layer(raster,paper){const canvas=this.createCanvas();paint(canvas.getContext('2d'),[],[],this.meta,{...this.style,raster,paper});return canvas;}
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
            value={base,moving,padding,width,height,canvas:this.layer(base,true),overlay};this.drag.frames.set(number,value);
        }
        return value;
    }
    paintExtra(context,extra,frame){
        let left=this.width,top=this.height,right=0,bottom=0;
        for(const stroke of extra)for(const point of stroke.points??[])if(Number.isFinite(point?.[0])&&Number.isFinite(point?.[1])){
            left=Math.min(left,Math.floor(point[0])-8);top=Math.min(top,Math.floor(point[1])-8);
            right=Math.max(right,Math.ceil(point[0])+9);bottom=Math.max(bottom,Math.ceil(point[1])+9);
        }
        left=Math.max(0,left);top=Math.max(0,top);right=Math.min(this.width,right);bottom=Math.min(this.height,bottom);
        const width=right-left,height=bottom-top;if(width<=0||height<=0)return;
        const canvas=this.previewCanvas??=this.createCanvas(),overlay=canvas.getContext('2d'),length=width*height;
        if(canvas.width!==width)canvas.width=width;if(canvas.height!==height)canvas.height=height;
        overlay.clearRect(0,0,width,height);
        if(!this.previewPixels||this.previewPixels.length<length)this.previewPixels=new Uint8Array(Math.max(length,(this.previewPixels?.length??256)*2));
        const pixels=this.previewPixels.subarray(0,length);pixels.fill(0);
        applyStrokes({pixels},extra.map(stroke=>({...stroke,points:(stroke.points??[]).map(point=>Array.isArray(point)?point.map((value,index)=>index===0?value-left:index===1?value-top:value):point)})),width,height,frame);
        paintPixels(overlay,{pixels},width,height,this.style.themeInk);context.drawImage(canvas,left,top);
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
            if(!erasing)context.drawImage(value.canvas,0,0);else raster={pixels:value.raster.pixels.slice()};
        }
        if(raster){applyStrokes(raster,extra,w,h,frame,this.ops.length);paint(context,[],[],this.meta,{...this.style,raster});}
        else if(extra.length)this.paintExtra(context,extra,frame);
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
    dispose(){this.frames.clear();this.drag=null;this.ops=[];this.previewCanvas=this.previewPixels=null;}
}
