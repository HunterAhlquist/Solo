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
// Two fingers tapped together undo, three redo. Anything longer or looser was
// the start of a pinch or a pan, or a palm, and is left to those handlers.
export class TouchTap {
    static slop=10;
    static maxDuration=350;
    fingers=new Map();
    startedAt=0;
    peak=0;
    spoiled=false;
    // True when this touch starts a new tap, so the caller can keep the view to restore.
    down(event){
        if(event.pointerType==='pen'){this.fingers.clear();return false;}
        if(event.pointerType!=='touch')return false;
        const starting=this.fingers.size===0;
        if(starting){this.startedAt=event.timeStamp;this.peak=0;this.spoiled=false;}
        this.fingers.set(event.pointerId,[event.clientX,event.clientY]);this.peak=Math.max(this.peak,this.fingers.size);
        return starting;
    }
    move(event){
        const start=this.fingers.get(event.pointerId);
        if(start&&Math.hypot(event.clientX-start[0],event.clientY-start[1])>TouchTap.slop)this.spoiled=true;
    }
    up(event){
        if(!this.fingers.has(event.pointerId))return null;
        if(event.type==='pointercancel')this.spoiled=true;else this.move(event);
        this.fingers.delete(event.pointerId);
        if(this.fingers.size)return null;
        if(this.spoiled||event.timeStamp-this.startedAt>TouchTap.maxDuration)return null;
        return this.peak===2?'undo':this.peak===3?'redo':null;
    }
}
