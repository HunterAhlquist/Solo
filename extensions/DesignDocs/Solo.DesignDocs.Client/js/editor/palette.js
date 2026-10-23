// Coordinates are measured together by CodeMirror, before writing any layout styles.
export function caretPalettePosition(caret,body,viewport,size,columnLeft){
    if(!caret||caret.bottom<viewport.top||caret.top>viewport.bottom)return null;
    const margin=8,left=Math.max(body.left,viewport.left)+margin,right=Math.min(body.right,viewport.right)-margin;
    const bottom=Math.min(body.bottom,viewport.bottom)-margin;
    return {left:Math.max(left,Math.min(columnLeft-size.width-8,right-size.width))-body.left,
        top:Math.max(Math.max(body.top,viewport.top)+margin,Math.min((caret.top+caret.bottom-size.width)/2,bottom-size.height))-body.top};
}
