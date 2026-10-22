export async function loadFont(url){const atlas=new Image();atlas.src=url;await atlas.decode();
    return (context,box)=>{const lines=box.text.split('\n'),scale=Math.max(1,Math.min(8,box.scale??2));
        const layer=document.createElement('canvas');layer.width=Math.max(1,...lines.map(l=>l.length))*6;layer.height=Math.max(1,lines.length)*8;
        const ctx=layer.getContext('2d');ctx.imageSmoothingEnabled=false;
        lines.forEach((line,y)=>[...line].forEach((c,x)=>{const code=c.charCodeAt(0),i=(code>=32&&code<128?code:63)-32;ctx.drawImage(atlas,i%16*6,Math.floor(i/16)*8,5,7,x*6,y*8,5,7);}));
        ctx.globalCompositeOperation='source-in';ctx.fillStyle=context.fillStyle;ctx.fillRect(0,0,layer.width,layer.height);
        context.drawImage(layer,Math.round(box.x),Math.round(box.y),layer.width*scale,layer.height*scale);
    };
}
