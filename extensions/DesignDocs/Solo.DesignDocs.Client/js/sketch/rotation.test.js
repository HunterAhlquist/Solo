import test from 'node:test';
import assert from 'node:assert/strict';
import { bindDesktopRotation } from './rotation.js';
import { pagePoint } from './viewport.js';
class Rotation extends Event {constructor(rotation,clientX=340,clientY=260){super('solo:rotate-gesture');this.detail={rotation,clientX,clientY};}}
function fixture(){
    const source=new EventTarget(),paper={closest:()=>false},input={closest:()=>true};let target=paper;
    const viewport={ownerDocument:{elementFromPoint:()=>target},contains:value=>value===paper||value===input,getBoundingClientRect:()=>({left:100,top:80})};
    let view={pan:[35,-20],zoom:1.2,angle:13};
    const stop=bindDesktopRotation(viewport,()=>view,value=>{view=value;},source);
    return {source,stop,view:()=>view,paper,input,target:value=>{target=value;}};
}
test('native incremental rotation preserves its anchor and follows both directions',()=>{
    const f=fixture(),anchor=[240,180],point=pagePoint(f.view(),anchor);
    f.source.dispatchEvent(new Rotation(12.5));assert.equal(f.view().angle,.5);
    f.source.dispatchEvent(new Rotation(-3.25,400,300));assert.equal(f.view().angle,3.75);
    const after=pagePoint(f.view(),anchor);point.forEach((value,i)=>assert.ok(Math.abs(value-after[i])<1e-8));
    assert.equal(f.view().zoom,1.2);
    f.source.dispatchEvent(new Rotation(0));const newAnchor=[300,220],nextPoint=pagePoint(f.view(),newAnchor);
    f.source.dispatchEvent(new Rotation(-1.125,400,300));assert.equal(f.view().angle,4.875);
    const next=pagePoint(f.view(),newAnchor);nextPoint.forEach((value,i)=>assert.ok(Math.abs(value-next[i])<1e-8));f.stop();
});
test('rotation ignores other surfaces and text inputs, validates data, and detaches on close',()=>{
    const f=fixture(),before=f.view();
    for(const target of [null,f.input]){f.target(target);f.source.dispatchEvent(new Rotation(15));assert.equal(f.view(),before);}
    f.target(f.paper);f.source.dispatchEvent(new Rotation(NaN));assert.equal(f.view(),before);
    f.stop();f.source.dispatchEvent(new Rotation(15));assert.equal(f.view(),before);
});
