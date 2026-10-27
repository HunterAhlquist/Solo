import test from 'node:test';import assert from 'node:assert/strict';
import {SketchScene} from './scene.js';
import {paint,rasterize,connectedRegion,splitSelection,applyStrokes} from './render.js';
const meta={pageSize:'A5',pattern:'grid'},stroke={id:'a',tool:'pen',ink:'theme',level:2,pointerType:'mouse',seed:27,points:[[5,20,1],[55,20,1]]};
// A tiny software canvas checks pixels and compositing, not merely method calls.
export function bitmapCanvas(){
    const canvas={width:0,height:0};
    const colors={'white':[255,255,255],'#111':[17,17,17],'#d0d0d0':[208,208,208],'#b52c32':[181,44,50],'#2164b4':[33,100,180],'#23763e':[35,118,62],'#8043ad':[128,67,173]};
    const context={canvas,globalAlpha:1,fillStyle:'white',fills:0,copies:0,buffer:null,
        clearRect(x=0,y=0,w=canvas.width,h=canvas.height){
            if(this.buffer?.length!==canvas.width*canvas.height*4){this.buffer=new Uint8ClampedArray(canvas.width*canvas.height*4);return;}
            for(let py=Math.max(0,y);py<Math.min(canvas.height,y+h);py++)this.buffer.fill(0,(py*canvas.width+Math.max(0,x))*4,(py*canvas.width+Math.min(canvas.width,x+w))*4);
        },
        fillRect(x,y,w,h){if(this.buffer?.length!==canvas.width*canvas.height*4)this.buffer=new Uint8ClampedArray(canvas.width*canvas.height*4);this.fills++;const rgb=colors[this.fillStyle];assert.ok(rgb,this.fillStyle);
            for(let py=Math.max(0,y);py<Math.min(canvas.height,y+h);py++)for(let px=Math.max(0,x);px<Math.min(canvas.width,x+w);px++){
                const at=(py*canvas.width+px)*4,a=this.globalAlpha,old=this.buffer[at+3]/255,out=a+old*(1-a);
                for(let c=0;c<3;c++)this.buffer[at+c]=(rgb[c]*a+this.buffer[at+c]*old*(1-a))/out;this.buffer[at+3]=out*255;
            }
        },
        // Both drawImage forms: (image,dx,dy) and an unscaled source rectangle.
        drawImage(source,...args){this.copies++;const sourceBuffer=source.getContext('2d').buffer;if(!sourceBuffer)return;
            const [x0,y0,w,h,dx,dy]=args.length===2?[0,0,source.width,source.height,...args]:args;
            if(this.buffer?.length!==canvas.width*canvas.height*4)this.buffer=new Uint8ClampedArray(canvas.width*canvas.height*4);
            for(let y=y0;y<y0+h;y++)for(let x=x0;x<x0+w;x++){
                const sx=x-x0+dx,sy=y-y0+dy;if(sx<0||sy<0||sx>=canvas.width||sy>=canvas.height||x>=source.width||y>=source.height)continue;
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
test('a held selection can leave every page edge and return without losing pixels',()=>{
    const scene=new SketchScene(bitmapCanvas);scene.setScene([stroke],meta);
    const selection={region:connectedRegion(scene.hitRaster(),420,595,10,20),ids:new Set(['a']),textIds:new Set(),dx:20,dy:20},target=output();
    scene.render(target,{selection});const before=target.buffer.slice();
    for(const [dx,dy]of [[-450,0],[500,0],[0,-650],[0,650],[0,0]]){selection.dx=dx;selection.dy=dy;scene.render(target,{selection});}
    selection.dx=20;selection.dy=20;scene.render(target,{selection});assert.deepEqual(target.buffer,before);
    assert.deepEqual(scene.ops,[stroke],'a preview must not mutate shared strokes');
});
test('a held selection keeps a complete floating preview outside the paper until release',()=>{
    const scene=new SketchScene(bitmapCanvas);scene.setScene([stroke],meta);const target=output(),floating=output();
    const selection={region:connectedRegion(scene.hitRaster(),420,595,10,20),ids:new Set(['a']),textIds:new Set(),dx:-100,dy:0};
    const placement=scene.render(target,{selection,selectionContext:floating});
    assert.ok(placement,'the held selection must have a preview independent of the paper clip');
    assert.ok(floating.buffer.some((v,i)=>i%4===3&&v>0),'off-page movement must retain the complete selection raster');
    const before=floating.buffer.slice();selection.dx=100;scene.render(target,{selection,selectionContext:floating});assert.deepEqual(floating.buffer,before);
    assert.equal(scene.render(target,{selection:{...selection,dx:0},selectionContext:floating}),null,'release returns drawing to the page surface');
});

test('purple strokes and text retain their color in the scene and exported painting',()=>{
    const ops=[{...stroke,ink:'purple'}],scene=new SketchScene(bitmapCanvas);scene.setScene(ops,meta);
    const texts=[{ink:'purple'}],font=context=>context.fillRect(100,100,2,2),live=output(),exported=output();
    scene.render(live,{texts,font});paint(exported,ops,texts,meta,{font,exporting:true});
    assert.deepEqual(live.buffer,exported.buffer);
    const pixel=(x,y)=>[...exported.buffer.slice((y*420+x)*4,(y*420+x)*4+4)];
    assert.deepEqual(pixel(20,20),[128,67,173,255]);assert.deepEqual(pixel(100,100),[128,67,173,255]);
});

test('live pen feedback does not copy committed rasters or allocate a page-sized overlay',()=>{
    const canvases=[],scene=new SketchScene(()=>{const canvas=bitmapCanvas();canvases.push(canvas);return canvas;});scene.setScene([stroke],{pageSize:'A3'});
    const target=output();scene.render(target);let copied=0;
    for(const raster of Object.values(scene.frame(0).raster)){const slice=raster.slice.bind(raster);raster.slice=(...args)=>{copied+=raster.byteLength;return slice(...args);};}
    scene.render(target,{extra:[{...stroke,points:[[100,100,1],[150,120,1]]}]});
    assert.equal(copied,0,'Pencil feedback must not copy the committed page');
    const overlay=canvases.at(-1);assert.ok(overlay.width*overlay.height<5000,'only the live stroke bounds need raster painting');
    scene.render(target,{extra:[{...stroke,tool:'eraser',points:[[30,0,1],[30,40,1]]}]});
    assert.equal(copied,842*1191,'eraser feedback can copy pixels but never owner identities');
});
test('bounded live pen layers agree with full replay across colors, frames, and page edges',()=>{
    const extra=[{...stroke,ink:'blue',points:[[-8,20,1],[45,25,1]]},{...stroke,ink:'purple',points:[[20,10,1],[55,35,1]]}];
    const scene=new SketchScene(bitmapCanvas);scene.setScene([stroke],meta);const target=output();
    for(const frame of [0,1,2]){scene.render(target,{extra,frame});const direct=output();paint(direct,[stroke,...extra],[],meta,{frame});assert.deepEqual(target.buffer,direct.buffer);}
});

test('appended strokes repaint cached frames to the pixels of a full replay',()=>{
    const second={...stroke,id:'b',ink:'red',points:[[100,200,.5],[160,260,.9]]},eraser={...stroke,id:'e',tool:'eraser',level:4,points:[[30,0,1],[30,40,1]]};
    for(const pattern of ['grid','dots','notebook','empty']){
        const page={...meta,pattern},scene=new SketchScene(bitmapCanvas),target=output();let ops=[stroke];
        scene.setScene(ops,page);for(const frame of [0,1,2])scene.render(target,{frame});const cached=scene.frame(0).canvas;
        for(const next of [second,eraser]){
            ops=[...ops,next];scene.setScene(ops,page);
            assert.equal(scene.frame(0).canvas,cached,'appending must not rebuild the cached layer');
            for(const frame of [0,1,2]){scene.render(target,{frame});const direct=output();paint(direct,ops,[],page,{frame});assert.deepEqual(target.buffer,direct.buffer,pattern+' frame '+frame);}
            assert.deepEqual(scene.hitRaster(),rasterize(ops,420,595,0));
        }
        scene.setScene(ops,page);assert.equal(scene.frame(0).canvas,cached,'an unchanged scene keeps its frames');
        // Concurrent writers can land a stroke before existing ones; that replays the page.
        ops=[second,...ops];scene.setScene(ops,page);assert.notEqual(scene.frame(0).canvas,cached);
        scene.render(target,{frame:1});const direct=output();paint(direct,ops,[],page,{frame:1});assert.deepEqual(target.buffer,direct.buffer);
    }
});
test('a growing held stroke extends its layer and matches a full replay each frame',()=>{
    const scene=new SketchScene(bitmapCanvas);scene.setScene([stroke],meta);const target=output();
    const held={...stroke,id:'held',ink:'green',pointerType:'pen',points:[[-20,300,.2]]},check=frame=>{const direct=output();paint(direct,[stroke,held],[],meta,{frame});assert.deepEqual(target.buffer,direct.buffer);};
    let layer;
    for(let i=1;i<140;i++){
        held.points.push([-20+i*4,300+Math.sin(i/5)*120,i%10/10]);scene.render(target,{extra:[held],frame:i%3});
        if(i>3&&i%3===0){const next=scene.held.get('held').frames.get(0);if(layer)assert.equal(next,layer,'a held stroke extends its layer instead of replaying');layer=next;}
        if(i%23===0)check(i%3);
    }
    check(139%3);
    // A collaborator's feed rewrites the tail after a gap; that layer starts over.
    held.points.length=100;held.points.push([200,100,.5]);scene.render(target,{extra:[held],frame:1});check(1);
    scene.render(target,{frame:1});assert.equal(scene.held.size,0,'a finished stroke releases its layers');
});
test('a held eraser extends one copy of the page and matches a full replay each frame',()=>{
    const page={pageSize:'A5',pattern:'dots'},ops=Array.from({length:12},(_,i)=>({...stroke,id:'s'+i,ink:['theme','red','blue'][i%3],points:[[10,30+i*40,1],[400,40+i*40,.5]]}));
    const scene=new SketchScene(bitmapCanvas),target=output(),check=(extra,frame)=>{const direct=output();paint(direct,[...ops,...extra],[],page,{frame});assert.deepEqual(target.buffer,direct.buffer);};
    scene.setScene(ops,page);for(const frame of [0,1,2])scene.render(target,{frame});
    let copies=0;for(const frame of [0,1,2]){const raster=scene.frame(frame).raster.pixels,slice=raster.slice.bind(raster);raster.slice=(...a)=>{copies++;return slice(...a);};}
    const eraser={id:'eraser',tool:'eraser',ink:'theme',level:4,pointerType:'pen',seed:5,points:[[30,20,.8]]};
    for(let i=1;i<90;i++){
        eraser.points.push([30+i*4,20+i*5+Math.sin(i/4)*20,.8]);scene.render(target,{extra:[eraser],frame:i%3});
        if(i%22===0)check([eraser],i%3);
    }
    assert.equal(copies,3,'each boil frame copies the page once per erase, not once per frame');
    const fills=scene.frame(1).erase.canvas.getContext('2d').fills;const tip=eraser.points.at(-1);eraser.points.push([tip[0]+3,tip[1]+2,.8]);scene.render(target,{extra:[eraser],frame:1});
    assert.ok(scene.frame(1).erase.canvas.getContext('2d').fills-fills<200,'a frame repaints only the new samples, not the paper and every ink run');
    check([eraser],1);
    // A held pen stroke draws over the erased copy.
    const pen={...stroke,id:'pen',ink:'green',pointerType:'pen',points:[[60,100,1],[300,400,1]]};
    scene.render(target,{extra:[eraser,pen],frame:2});check([eraser,pen],2);
    // The collaborator feed rewrote the tail: start over from the committed page.
    eraser.points.length=40;eraser.points.push([100,500,.8]);scene.render(target,{extra:[eraser],frame:0});check([eraser],0);
    // Saving the erase appends it; the page then matches with no held eraser.
    const saved=[...ops,eraser];scene.setScene(saved,page);scene.render(target,{frame:0});assert.equal(scene.frame(0).erase,null);
    const direct=output();paint(direct,saved,[],page,{frame:0});assert.deepEqual(target.buffer,direct.buffer);
});
test('an eraser lifted before its stroke arrives shows the saved page again',()=>{
    const scene=new SketchScene(bitmapCanvas),target=output();scene.setScene([stroke],meta);
    const eraser={...stroke,id:'e',tool:'eraser',level:4,pointerType:'pen',points:[[30,0,1],[30,40,1]]};
    scene.render(target,{extra:[eraser]});const other={...eraser,id:'f',points:[[10,0,1],[10,40,1]]};
    scene.render(target,{extra:[other]});const direct=output();paint(direct,[stroke,other],[],meta);assert.deepEqual(target.buffer,direct.buffer);
});
test('the paper and its ruling are drawn once per page and style',()=>{
    const page={pageSize:'A4',pattern:'dots'},scene=new SketchScene(bitmapCanvas),target=output();scene.setScene([stroke],page);
    for(const frame of [0,1,2])scene.render(target,{frame});
    const paper=scene.paperCanvas.getContext('2d'),dots=paper.fills;assert.ok(dots>3000);
    scene.setScene([stroke,{...stroke,id:'b',points:[[100,100,1],[120,140,1]]}],page);for(const frame of [0,1,2])scene.render(target,{frame});
    for(const frame of [0,1,2])assert.ok(scene.frame(frame).canvas.getContext('2d').fills<dots,'layers copy the paper instead of redrawing each dot');
    assert.equal(paper.fills,dots,'appending a stroke does not redraw the paper');
    scene.setStyle({paperColor:'#111'});scene.render(target);assert.ok(paper.fills>dots,'a new paper colour redraws it');
});
test('a page resized under a held stroke starts its layers over',()=>{
    const scene=new SketchScene(bitmapCanvas),target=output(),check=page=>{const direct=output();paint(direct,[stroke,held],[],page);assert.deepEqual(target.buffer,direct.buffer);};
    const a3={pageSize:'A3',pattern:'grid'},a5={pageSize:'A5',pattern:'grid'},held={...stroke,id:'held',pointerType:'pen',points:[[500,700,1],[700,900,1]]};
    scene.setScene([stroke],a3);scene.render(target,{extra:[held]});check(a3);
    // A collaborator shrinks the page mid-stroke, then the stroke goes on.
    scene.setScene([stroke],a5);held.points.push([200,300,1]);scene.render(target,{extra:[held]});check(a5);
    // Enlarging again shows the samples the smaller page had clipped.
    scene.setScene([stroke],a3);held.points.push([210,320,1]);scene.render(target,{extra:[held]});check(a3);
});
test('a collaborator stroke redraws when the feed fills in earlier samples',()=>{
    const scene=new SketchScene(bitmapCanvas),target=output();scene.setScene([stroke],meta);
    const full=Array.from({length:60},(_,i)=>[20+i*5,200+Math.sin(i/6)*60,.5]),held={...stroke,id:'feed',pointerType:'pen',revision:1,points:[]};
    full.slice(30).forEach((point,i)=>{held.points[30+i]=point;});scene.render(target,{extra:[held]});
    full.slice(0,30).forEach((point,i)=>{held.points[i]=point;});held.revision=2;scene.render(target,{extra:[held]});
    const direct=output();paint(direct,[stroke,{...held,points:full}],[],meta);assert.deepEqual(target.buffer,direct.buffer);
});
