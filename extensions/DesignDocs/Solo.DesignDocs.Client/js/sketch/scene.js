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
    render(context,{frame=0,texts=[],extra=[],selection=null,font}={}){
        const {width:w,height:h}=this,moving=selection&&(selection.dx||selection.dy);
        if(context.canvas.width!==w)context.canvas.width=w;if(context.canvas.height!==h)context.canvas.height=h;
        context.clearRect(0,0,w,h);context.imageSmoothingEnabled=false;
        let raster;
        if(moving){
            const value=this.dragFrame(selection,frame);
            if(!extra.length){context.drawImage(value.canvas,0,0);context.drawImage(value.overlay,selection.dx-value.padding,selection.dy-value.padding);}
            else{
                raster={pixels:value.base.pixels.slice(),owners:value.base.owners.slice()};
                for(let y=0;y<value.height;y++){const targetY=y+selection.dy-value.padding;if(targetY<0||targetY>=h)continue;
                    for(let x=0;x<value.width;x++){const targetX=x+selection.dx-value.padding,ink=value.moving.pixels[y*value.width+x];if(ink&&targetX>=0&&targetX<w)raster.pixels[targetY*w+targetX]=ink;}}
            }
        }else{
            this.drag=null;const value=this.frame(frame);
            if(!extra.length)context.drawImage(value.canvas,0,0);else raster={pixels:value.raster.pixels.slice(),owners:value.raster.owners.slice()};
        }
        if(raster){applyStrokes(raster,extra,w,h,frame,this.ops.length);paint(context,[],[],this.meta,{...this.style,raster});}
        const boxes=moving?texts.map(t=>selection.textIds.has(t.id)?{...t,x:t.x+selection.dx,y:t.y+selection.dy}:t):texts;
        paintTexts(context,boxes,{...this.style,font});
    }
    dispose(){this.frames.clear();this.drag=null;this.ops=[];}
}
