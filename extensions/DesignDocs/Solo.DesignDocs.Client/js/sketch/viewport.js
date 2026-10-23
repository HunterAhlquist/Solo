const clamp=value=>Math.max(.1,Math.min(16,value));
export function pagePoint(view,point){
    const x=(point[0]-view.pan[0])/view.zoom,y=(point[1]-view.pan[1])/view.zoom,a=-view.angle*Math.PI/180;
    return [Math.cos(a)*x-Math.sin(a)*y,Math.sin(a)*x+Math.cos(a)*y];
}
export function anchoredView(view,anchor,zoom=view.zoom,angle=view.angle,target=anchor){
    const p=pagePoint(view,anchor),a=angle*Math.PI/180,z=clamp(zoom);
    return {zoom:z,angle,pan:[target[0]-(Math.cos(a)*p[0]-Math.sin(a)*p[1])*z,target[1]-(Math.sin(a)*p[0]+Math.cos(a)*p[1])*z]};
}
export function wheelView(view,event,point){
    const unit=event.deltaMode===1?16:event.deltaMode===2?600:1,dx=event.deltaX*unit,dy=event.deltaY*unit;
    if(event.ctrlKey||event.metaKey)return anchoredView(view,point,view.zoom*Math.exp(-dy*.005));
    if(event.altKey)return anchoredView(view,point,view.zoom,view.angle+dy*.15);
    return {...view,pan:[view.pan[0]-(event.shiftKey&&dx===0?dy:dx),view.pan[1]-(event.shiftKey&&dx===0?0:dy)]};
}
export function touchView(initial,a,b){
    const middle=[(a[0]+b[0])/2,(a[1]+b[1])/2];
    const delta=Math.atan2(b[1]-a[1],b[0]-a[0])-initial.rotation;
    const rotation=Math.atan2(Math.sin(delta),Math.cos(delta))*180/Math.PI;
    return anchoredView(initial,initial.mid,initial.zoom*Math.hypot(a[0]-b[0],a[1]-b[1])/Math.max(1,initial.distance),initial.angle+rotation,middle);
}
