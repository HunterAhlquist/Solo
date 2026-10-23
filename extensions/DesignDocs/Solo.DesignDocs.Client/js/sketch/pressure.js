export const touchSupported=navigator=>Number(navigator?.maxTouchPoints)>0;
export function trackpadPressure(event,constants){
    const force=event.webkitForce,minimum=constants.WEBKIT_FORCE_AT_MOUSE_DOWN,maximum=constants.WEBKIT_FORCE_AT_FORCE_MOUSE_DOWN;
    if(!Number.isFinite(force)||!Number.isFinite(minimum)||!Number.isFinite(maximum)||maximum<=minimum)return null;
    return Math.max(0,Math.min(1,(force-minimum)/(maximum-minimum)));
}
export function applyTrackpadPressure(stroke,event,constants){
    const pressure=trackpadPressure(event,constants);
    if(!stroke||!['mouse','trackpad'].includes(stroke.pointerType)||pressure===null)return null;
    // Ordinary Safari mice also report the click threshold; that is not pressure hardware.
    if(stroke.pointerType==='mouse'&&pressure===0)return null;
    stroke.pointerType='trackpad';return pressure;
}
