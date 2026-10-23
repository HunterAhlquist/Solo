// Coordinates are measured together by CodeMirror, before writing any layout styles.
export function caretPalettePosition(caret,body,viewport,size){
    if(!caret||caret.bottom<viewport.top||caret.top>viewport.bottom||caret.left<viewport.left||caret.left>viewport.right)return null;
    const margin=8,left=Math.max(body.left,viewport.left)+margin,right=Math.min(body.right,viewport.right)-margin;
    const bottom=Math.min(body.bottom,viewport.bottom)-margin;
    return {left:Math.max(left,Math.min(caret.left,right-size.width))-body.left,
        top:Math.max(Math.max(body.top,viewport.top)+margin,Math.min(caret.bottom+8,bottom-size.height))-body.top};
}
