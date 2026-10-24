export function textSize(box){return Math.max(8,Math.min(64,box.fontSize??(box.scale??2)*8));}
export function textLayout(context,box,family){
    const size=textSize(box),lineHeight=size*1.5,lines=String(box.text??'').split('\n');
    context.font=`${size}px ${family}`;
    return {size,lineHeight,lines,width:Math.max(1,...lines.map(line=>context.measureText(line).width)),height:lines.length*lineHeight};
}
export async function loadFont(root=document.body){
    await document.fonts.ready;
    const font=(context,box)=>{
        const family=getComputedStyle(root).fontFamily,layout=textLayout(context,box,family);
        context.textBaseline='top';
        layout.lines.forEach((line,index)=>context.fillText(line,box.x,box.y+index*layout.lineHeight+(layout.lineHeight-layout.size)/2));
    };
    font.bounds=(context,box)=>{const layout=textLayout(context,box,getComputedStyle(root).fontFamily);return {left:box.x,top:box.y,right:box.x+layout.width,bottom:box.y+layout.height};};
    return font;
}
