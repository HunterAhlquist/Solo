import test from 'node:test';import assert from 'node:assert/strict';
import {SketchScene} from './scene.js';
import {paint,rasterize,connectedRegion,splitSelection,applyStrokes} from './render.js';
const meta={pageSize:'A5',pattern:'grid'},stroke={id:'a',tool:'pen',ink:'theme',level:2,pointerType:'mouse',seed:27,points:[[5,20,1],[55,20,1]]};
// A tiny software canvas checks pixels and compositing, not merely method calls.
export function bitmapCanvas(){
    const canvas={width:0,height:0};
    const colors={'white':[255,255,255],'#111':[17,17,17],'#d0d0d0':[208,208,208],'#b52c32':[181,44,50],'#2164b4':[33,100,180],'#23763e':[35,118,62]};
    const context={canvas,globalAlpha:1,fillStyle:'white',fills:0,copies:0,buffer:null,
        clearRect(){this.buffer=new Uint8ClampedArray(canvas.width*canvas.height*4);},
        fillRect(x,y,w,h){if(this.buffer?.length!==canvas.width*canvas.height*4)this.buffer=new Uint8ClampedArray(canvas.width*canvas.height*4);this.fills++;const rgb=colors[this.fillStyle];assert.ok(rgb,this.fillStyle);
            for(let py=Math.max(0,y);py<Math.min(canvas.height,y+h);py++)for(let px=Math.max(0,x);px<Math.min(canvas.width,x+w);px++){
                const at=(py*canvas.width+px)*4,a=this.globalAlpha,old=this.buffer[at+3]/255,out=a+old*(1-a);
                for(let c=0;c<3;c++)this.buffer[at+c]=(rgb[c]*a+this.buffer[at+c]*old*(1-a))/out;this.buffer[at+3]=out*255;
            }
        },
        drawImage(source,dx,dy){this.copies++;const sourceBuffer=source.getContext('2d').buffer;
            for(let y=0;y<source.height;y++)for(let x=0;x<source.width;x++){
                const sx=x+dx,sy=y+dy;if(sx<0||sy<0||sx>=canvas.width||sy>=canvas.height)continue;
                const from=(y*source.width+x)*4;if(!sourceBuffer[from+3])continue;
                this.buffer.set(sourceBuffer.subarray(from,from+4),(sy*canvas.width+sx)*4);
            }
        },
    };canvas.getContext=()=>context;return canvas;
}
function output(){return bitmapCanvas().getContext('2d');}
function reference(ops){const context=output();paint(context,ops,[],meta);return context.buffer;}
test('cached boil frames and active erasers match a full scene replay',()=>{
    const scene=new SketchScene(bitmapCanvas);scene.setScene([stroke],meta);
    const target=output();for(const frame of [0,1,2,0,2]){scene.render(target,{frame});const direct=output();paint(direct,[stroke],[],meta,{frame});assert.deepEqual(target.buffer,direct.buffer);}
    const eraser={...stroke,tool:'eraser',level:4,points:[[30,0,1],[30,40,1]]};scene.render(target,{extra:[eraser]});assert.deepEqual(target.buffer,reference([stroke,eraser]));scene.render(target);assert.deepEqual(target.buffer,reference([stroke]));
    const appended=applyStrokes(rasterize([stroke],420,595),[eraser],420,595,0,1);assert.deepEqual(appended,rasterize([stroke,eraser],420,595));
});
test('pointer feedback reuses committed frames; style and scene changes invalidate the right cache',()=>{
    const scene=new SketchScene(bitmapCanvas);scene.setScene([stroke],meta);const target=output();scene.render(target);
    const first=scene.frame(0),bitmap=first.canvas,mask=scene.hitRaster();const fills=bitmap.getContext('2d').fills;
    scene.render(target);assert.equal(scene.frame(0),first);assert.equal(bitmap.getContext('2d').fills,fills);assert.equal(target.fills,0);
    scene.setStyle({themeInk:'#b52c32'});scene.render(target);assert.equal(scene.hitRaster(),mask);assert.notEqual(scene.frame(0).canvas,bitmap);
    scene.setScene([],meta);scene.render(target);assert.notEqual(scene.hitRaster(),mask);assert.ok(scene.hitRaster().pixels.every(p=>p===0));
    scene.dispose();assert.equal(scene.frames.size,0);
});
test('moving a cut selection reuses layers and agrees with the committed split',()=>{
    const eraser={...stroke,id:'eraser',tool:'eraser',level:4,points:[[30,0,1],[30,40,1]]},ops=[stroke,eraser],scene=new SketchScene(bitmapCanvas);scene.setScene(ops,meta);
    const region=connectedRegion(scene.hitRaster(),420,595,10,20),selection={region,ids:new Set(['a']),textIds:new Set(),dx:0,dy:12},target=output();scene.render(target,{selection});
    const layer=scene.dragFrame(selection,0),parts=splitSelection(ops,region,420,595,0,12,selection.ids);assert.deepEqual(target.buffer,reference(parts.stationary.concat(parts.moved)));
    selection.dx=10;scene.render(target,{selection});assert.equal(scene.dragFrame(selection,0),layer);
});
test('paper is opaque and only the grid pattern is half transparent',()=>{
    const target=output();paint(target,[],[],meta);const at=(x,y)=>(y*420+x)*4;
    assert.deepEqual([...target.buffer.slice(at(10,10),at(10,10)+4)],[255,255,255,255]);
    assert.deepEqual([...target.buffer.slice(at(10,24),at(10,24)+4)],[232,232,232,255]);
});
test('selection preview keeps clipped stroke edges when moving them back onto the page',()=>{
    const ops=[{...stroke,level:4,points:[[-8,20,1],[40,20,1]]}],scene=new SketchScene(bitmapCanvas);scene.setScene(ops,meta);
    const region=connectedRegion(scene.hitRaster(),420,595,5,20),selection={region,ids:new Set(['a']),textIds:new Set(),dx:20,dy:0},target=output();scene.render(target,{selection});
    const parts=splitSelection(ops,region,420,595,20,0,selection.ids);assert.deepEqual(target.buffer,reference(parts.stationary.concat(parts.moved)));
});
