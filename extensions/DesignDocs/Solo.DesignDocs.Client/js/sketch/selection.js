export function contains(bounds,point){return bounds&&point[0]>=bounds.left&&point[0]<=bounds.right&&point[1]>=bounds.top&&point[1]<=bounds.bottom;}
export function polygonContains(points,x,y){
    let inside=false;
    for(let i=0,j=points.length-1;i<points.length;j=i++){
        const [ax,ay]=points[i],[bx,by]=points[j];
        if((ay>y)!==(by>y)&&x<(bx-ax)*(y-ay)/(by-ay)+ax)inside=!inside;
    }
    return inside;
}
export function selectionBounds(region,width,textBounds=[]){
    let left=Infinity,top=Infinity,right=-Infinity,bottom=-Infinity;
    for(let i=0;i<(region?.mask.length??0);i++)if(region.mask[i]){const x=i%width,y=Math.floor(i/width);left=Math.min(left,x);right=Math.max(right,x+1);top=Math.min(top,y);bottom=Math.max(bottom,y+1);}
    for(const box of textBounds){left=Math.min(left,box.left);top=Math.min(top,box.top);right=Math.max(right,box.right);bottom=Math.max(bottom,box.bottom);}
    return Number.isFinite(left)?{left,top,right,bottom}:null;
}
export function selectPolygon(raster,width,height,points,texts=[]){
    const mask=new Uint8Array(width*height),owners=new Set(),textIds=new Set();
    for(let y=0;y<height;y++)for(let x=0;x<width;x++)if(raster.pixels[y*width+x]&&polygonContains(points,x+.5,y+.5)){mask[y*width+x]=1;owners.add(raster.owners[y*width+x]);}
    for(const box of texts){const b=box.bounds;
        // Crossing a text object's box selects that object as a whole.
        const corners=[[b.left,b.top],[b.right,b.top],[b.right,b.bottom],[b.left,b.bottom],[(b.left+b.right)/2,(b.top+b.bottom)/2]];
        if(corners.some(p=>polygonContains(points,...p))||points.some(p=>contains(b,p)))textIds.add(box.id);
    }
    return {region:{mask,owners},textIds};
}
export function shiftRegion(region,width,height,dx,dy){
    const mask=new Uint8Array(width*height);
    for(let i=0;i<region.mask.length;i++)if(region.mask[i]){const x=i%width+dx,y=Math.floor(i/width)+dy;if(x>=0&&y>=0&&x<width&&y<height)mask[y*width+x]=1;}
    return {...region,mask};
}
