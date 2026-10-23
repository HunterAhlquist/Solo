import test from 'node:test';
import assert from 'node:assert/strict';
import {pagePoint,anchoredView,wheelView,touchView} from './viewport.js';
const view={pan:[35,-20],zoom:1.2,angle:13},anchor=[245,180];
function samePoint(a,b){a.forEach((v,i)=>assert.ok(Math.abs(v-b[i])<1e-8));}
test('fractional zoom and rotation keep the page point under the cursor',()=>{
    const next=anchoredView(view,anchor,1.37,27.25);
    samePoint(pagePoint(view,anchor),pagePoint(next,anchor));assert.equal(next.zoom,1.37);assert.equal(next.angle,27.25);
});
test('trackpad scroll pans; Ctrl/Cmd zoom; Alt rotates; Shift scrolls horizontally',()=>{
    const wheel={deltaX:0,deltaY:8,deltaMode:0};
    assert.deepEqual(wheelView(view,wheel,anchor).pan,[35,-28]);
    assert.deepEqual(wheelView(view,{...wheel,shiftKey:true},anchor).pan,[27,-20]);
    for(const key of ['ctrlKey','metaKey']){
        const next=wheelView(view,{...wheel,[key]:true},anchor);assert.ok(next.zoom<view.zoom&&next.zoom>.1);samePoint(pagePoint(view,anchor),pagePoint(next,anchor));
    }
    const rotated=wheelView(view,{...wheel,altKey:true},anchor);assert.equal(rotated.angle,14.2);samePoint(pagePoint(view,anchor),pagePoint(rotated,anchor));
});
test('two-finger gestures pan, scale and rotate around their initial midpoint',()=>{
    const initial={...view,mid:[100,100],distance:100,rotation:0};
    const next=touchView(initial,[125,100],[125,225]);
    assert.equal(next.zoom,1.5);assert.equal(next.angle,103);samePoint(pagePoint(initial,[100,100]),pagePoint(next,[125,162.5]));
});
