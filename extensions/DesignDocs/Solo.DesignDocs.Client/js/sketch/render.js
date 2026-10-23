export const sizes={A4:[595,842],A5:[420,595],A3:[842,1191],Letter:[612,792]};
export const widths=[1,2,3,5,8];
export function pageSize(meta){const size=sizes[meta.pageSize]??sizes.A4;return meta.orientation==='landscape'?[size[1],size[0]]:[...size];}
export function pressureWidth(level,pressure,type){const base=widths[Math.max(0,Math.min(4,level))];return type==='mouse'?base:Math.round(base+(8-base)*Math.max(0,Math.min(1,pressure)));}
export function jitter(seed,index,frame){let value=(seed^(index*374761393)^(frame*668265263))|0;value=Math.imul(value^(value>>>13),1274126177);return [(value>>>0)%3-1,(value>>>8)%3-1];}
export function* samples(stroke){const points=Array.isArray(stroke?.points)?stroke.points:[];let budget=65536;for(let i=0;i<points.length&&budget>0;i++){
    const a=points[Math.max(0,i-1)],b=points[i];if(!Array.isArray(a)||!Array.isArray(b)||![...a,...b].every(Number.isFinite))continue;const steps=Math.min(4096,Math.max(1,Math.ceil(Math.hypot(b[0]-a[0],b[1]-a[1]))));
    for(let n=i?1:0;n<=steps&&budget-->0;n++){const t=n/steps;yield [a[0]+(b[0]-a[0])*t,a[1]+(b[1]-a[1])*t,a[2]+(b[2]-a[2])*t];}
}}
function stamp(x,y,width,put){const first=-Math.floor(width/2),radius=width/2;
    for(let dy=0;dy<width;dy++)for(let dx=0;dx<width;dx++){
        const cx=dx+first,cy=dy+first;if(width>2&&Math.abs(cx)+Math.abs(cy)>radius*1.5)continue;put(Math.round(x)+cx,Math.round(y)+cy);
    }
}
export function rasterize(ops,width,height,frame=0){
    return applyStrokes({pixels:new Uint8Array(width*height),owners:new Int32Array(width*height)},ops,width,height,frame);
}
export function applyStrokes(raster,ops,width,height,frame=0,ownerOffset=0){const {pixels,owners}=raster;
    ops.forEach((stroke,owner)=>{let index=0,previous,paintBudget=262144;for(const [x,y,p] of samples(stroke)){
        if(paintBudget<=0)break;const [dx,dy]=jitter(stroke.seed,index++,frame),size=pressureWidth(stroke.level,p,stroke.pointerType);
        const target=[x+dx,y+dy],start=previous??target,steps=Math.min(4096,Math.max(1,Math.ceil(Math.hypot(target[0]-start[0],target[1]-start[1]))));
        for(let n=0;n<=steps&&paintBudget-->0;n++)stamp(start[0]+(target[0]-start[0])*n/steps,start[1]+(target[1]-start[1])*n/steps,size,(px,py)=>{if(px<0||py<0||px>=width||py>=height)return;const at=py*width+px;pixels[at]=stroke.tool==='eraser'?0:({theme:1,red:2,blue:3,green:4,purple:5}[stroke.ink]??1);owners[at]=pixels[at]?owner+ownerOffset+1:0;});
        previous=target;
    }});return raster;
}
export function connectedRegion(raster,width,height,x,y){let at=Math.round(y)*width+Math.round(x);
    if(!raster.pixels[at]){const near=[];for(let dy=-2;dy<=2;dy++)for(let dx=-2;dx<=2;dx++){const px=Math.round(x)+dx,py=Math.round(y)+dy;if(px>=0&&py>=0&&px<width&&py<height&&raster.pixels[py*width+px])near.push(py*width+px);}if(!near.length)return null;at=near[0];}
    const mask=new Uint8Array(width*height),queue=new Int32Array(width*height),owners=new Set();let read=0,write=0;queue[write++]=at;mask[at]=1;
    while(read<write){const p=queue[read++];owners.add(raster.owners[p]);const px=p%width;
        for(const next of [px>0?p-1:-1,px<width-1?p+1:-1,p>=width?p-width:-1,p+width<mask.length?p+width:-1,px>0&&p>=width?p-width-1:-1,px<width-1&&p>=width?p-width+1:-1,px>0&&p+width<mask.length?p+width-1:-1,px<width-1&&p+width<mask.length?p+width+1:-1])if(next>=0&&!mask[next]&&raster.pixels[next]){mask[next]=1;queue[write++]=next;}
    }return {mask,owners};
}
export function splitSelection(ops,region,width,height,dx,dy,selectedIds=null){const result=[],moved=[];
    ops.forEach((stroke,index)=>{
        if(stroke.tool==='eraser'||(selectedIds?!selectedIds.has(stroke.id):!region.owners.has(index+1))){result.push(stroke);return;}
        let segment=[],selected=null;
        const finish=()=>{if(!segment.length)return;const copy={...stroke,id:`${stroke.id}-${result.length}-${moved.length}`,points:segment};(selected?moved:result).push(copy);segment=[];};
        for(const point of samples(stroke)){
            let hit=false;const radius=Math.ceil(pressureWidth(stroke.level,point[2],stroke.pointerType)/2)+2;
            // The selection is rasterized after stamp jitter and interpolation.
            // Include that footprint when assigning centerline samples to a cut piece.
            for(let oy=-radius;oy<=radius&&!hit;oy++)for(let ox=-radius;ox<=radius&&!hit;ox++){
                const x=Math.round(point[0])+ox,y=Math.round(point[1])+oy;if(x>=0&&y>=0&&x<width&&y<height&&region.mask[y*width+x])hit=true;
            }
            if(selected!==null&&selected!==hit)finish();selected=hit;segment.push(hit?[point[0]+dx,point[1]+dy,point[2]]:point);
        }finish();
    });return {stationary:result,moved};
}
export function splitMove(...args){const {stationary,moved}=splitSelection(...args);return stationary.concat(moved);}
export function shape(tool,a,b,constrain=false){let dx=b[0]-a[0],dy=b[1]-a[1];if(constrain){const n=Math.max(Math.abs(dx),Math.abs(dy));dx=Math.sign(dx||1)*n;dy=Math.sign(dy||1)*n;}
    if(tool==='rectangle')return [[a[0],a[1],1],[a[0]+dx,a[1],1],[a[0]+dx,a[1]+dy,1],[a[0],a[1]+dy,1],[a[0],a[1],1]];
    const result=[];for(let i=0;i<=64;i++){const angle=i*Math.PI/32;result.push([a[0]+dx/2+Math.cos(angle)*dx/2,a[1]+dy/2+Math.sin(angle)*dy/2,1]);}return result;
}
export function paint(context,ops,texts,meta,{frame=0,themeInk='#111',font,exporting=false,paperColor='white',raster=null,paper=true}={}){
    const [width,height]=pageSize(meta);if(context.canvas.width!==width)context.canvas.width=width;if(context.canvas.height!==height)context.canvas.height=height;
    context.clearRect(0,0,width,height);context.imageSmoothingEnabled=false;context.fillStyle=paperColor;if(paper)context.fillRect(0,0,width,height);context.globalAlpha=.5;
    context.fillStyle='#d0d0d0';if(paper&&(meta.pattern==='grid'||meta.pattern==='notebook')){for(let y=24;y<height;y+=24)context.fillRect(0,y,width,1);if(meta.pattern==='grid')for(let x=24;x<width;x+=24)context.fillRect(x,0,1,height);}
    if(paper&&meta.pattern==='dots')for(let y=12;y<height;y+=12)for(let x=12;x<width;x+=12)context.fillRect(x,y,1,1);
    context.globalAlpha=1;
    raster??=rasterize(ops,width,height,frame);
    paintPixels(context,raster,width,height,themeInk);
    paintTexts(context,texts,{font,themeInk});return raster;
}
export function paintPixels(context,raster,width,height,themeInk='#111'){
    const palette=['',themeInk,'#b52c32','#2164b4','#23763e','#8043ad'];
    // Batch adjacent pixels into row spans instead of issuing a call per pixel.
    for(let y=0;y<height;y++)for(let x=0;x<width;){
        const ink=raster.pixels[y*width+x],start=x++;
        while(x<width&&raster.pixels[y*width+x]===ink)x++;
        if(ink){context.fillStyle=palette[ink];context.fillRect(start,y,x-start,1);}
    }
}
export function paintTexts(context,texts,{font,themeInk='#111'}={}){
    const palette={theme:themeInk,red:'#b52c32',blue:'#2164b4',green:'#23763e',purple:'#8043ad'};
    if(font)for(const text of texts){context.fillStyle=palette[text.ink]??themeInk;font(context,text);}
}
