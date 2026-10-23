// Pencil has priority over touch. A palm lifting must not release the pen lock.
export class SketchInput {
    touchDrawing=false;
    penId=null;
    ignored=new Set();
    start(event){
        if(event.pointerType==='touch'){
            if(this.penId!==null){this.ignored.add(event.pointerId);return 'ignore';}
            return this.touchDrawing?'draw':'pan';
        }
        if(event.pointerType==='pen')this.penId=event.pointerId;
        return 'draw';
    }
    ignores(event){return event.pointerType==='touch'&&(this.penId!==null||this.ignored.has(event.pointerId));}
    finish(event){const ignored=this.ignores(event);this.ignored.delete(event.pointerId);if(this.penId===event.pointerId)this.penId=null;return ignored;}
}
