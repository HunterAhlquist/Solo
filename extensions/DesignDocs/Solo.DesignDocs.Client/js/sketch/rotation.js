import { anchoredView } from './viewport.js';

export function bindDesktopRotation(viewport,getView,setView,source=window) {
    let anchor;
    const rotate=event=>{
        const {rotation,clientX,clientY}=event.detail??{};
        if(![rotation,clientX,clientY].every(Number.isFinite))return;
        if(rotation===0){anchor=null;return;}
        const target=viewport.ownerDocument.elementFromPoint(clientX,clientY);
        if(!viewport.contains(target)||target?.closest('textarea,input,[contenteditable=true]')){anchor=null;return;}
        const bounds=viewport.getBoundingClientRect();
        anchor??=[clientX-bounds.left,clientY-bounds.top];
        const view=getView();
        // AppKit measures counterclockwise deltas; CSS angles run clockwise.
        setView(anchoredView(view,anchor,view.zoom,view.angle-rotation));
    };
    source.addEventListener('solo:rotate-gesture',rotate);
    return ()=>source.removeEventListener('solo:rotate-gesture',rotate);
}
