// Match the board's 320 ms hold, 10 px touch slop, and 6 px fine-pointer slop.
export class TreeDragIntent {
    constructor(lift,setTimer=(callback,ms)=>setTimeout(callback,ms),clearTimer=timer=>clearTimeout(timer)){this.lift=lift;this.setTimer=setTimer;this.clearTimer=clearTimer;}
    begin(event,item){
        this.finish();this.press={id:event.pointerId,type:event.pointerType,start:[event.clientX,event.clientY],point:[event.clientX,event.clientY],item,active:false};
        if(event.pointerType==='touch')this.timer=this.setTimer(()=>this.activate(),320);
    }
    activate(){if(!this.press||this.press.active)return;this.press.active=true;this.lift(this.press);}
    move(event){
        const press=this.press;if(!press||press.id!==event.pointerId)return;
        press.point=[event.clientX,event.clientY];if(press.active)return;
        const distance=Math.hypot(event.clientX-press.start[0],event.clientY-press.start[1]);
        if(press.type==='touch'){if(distance>10)this.finish();}else if(distance>6)this.activate();
    }
    finish(){this.clearTimer(this.timer);this.timer=null;const press=this.press;this.press=null;return press;}
}
