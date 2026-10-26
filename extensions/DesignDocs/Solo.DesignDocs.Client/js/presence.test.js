import test from 'node:test';import assert from 'node:assert/strict';
import {presenceCursor,SketchPresence,viewportPoint,pointerOutline} from './presence.js';
import {installDOM} from './sketch/input-dom-fixture.js';
const user={userId:'11111111-1111-1111-1111-111111111111',name:'Alex Example',avatarUrl:'https://another-desktop.invalid/api/users/11111111-1111-1111-1111-111111111111/avatar',color:'#d2676e'};
test('merged cursors use the assigned outline and the receiving instance avatar proxy',t=>{
    const dom=installDOM();t.after(()=>dom.restore());
    for(const kind of ['pointer','text']){
        const cursor=presenceCursor(kind,user);assert.equal(cursor.style['--design-docs-user-color'],user.color);
        assert.equal(cursor.querySelector('img').src,`api/users/${user.userId}/avatar`,'a remote desktop localhost port must never become our image origin');
        assert.equal(cursor.querySelector('.design-docs-cursor-avatar').textContent,'AE');cursor.querySelector('img').emit('error');assert.equal(cursor.querySelector('img'),null);
    }
});
test('sketch presence transforms cursors with the page, scales brush footprints and removes departed peers',t=>{
    const dom=installDOM();t.after(()=>dom.restore());const peers=new SketchPresence(dom.root),view={pan:[10,20],zoom:2,angle:90};
    peers.render([[1,{user,pointer:{x:10,y:20,tool:'pen',width:8}}]],view);
    const entry=peers.markers.get(1),point=viewportPoint(view,[10,20]);assert.ok(Math.abs(point[0]+30)<1e-8&&Math.abs(point[1]-40)<1e-8);
    assert.equal(entry.ring.style.width,'16px');assert.equal(entry.ring.hidden,false);assert.equal(entry.root.style['--design-docs-user-color'],user.color);
    peers.render([[1,{user,pointer:{x:10,y:20,tool:'select'}}]],view);assert.equal(entry.ring.hidden,true);
    peers.render([[1,{user,pointer:null}]],view);assert.equal(peers.markers.size,0);
    peers.render([[1,{user,pointer:{x:NaN,y:0}}]],view);assert.equal(peers.markers.size,0);peers.dispose();assert.equal(dom.root.children.length,0);
});

function animatedPresence(t){
    const dom=installDOM();t.after(()=>dom.restore());let now=0,next=0;const frames=new Map(),reduced={matches:false};
    t.mock.method(performance,'now',()=>now);t.mock.method(globalThis,'matchMedia',()=>reduced);
    t.mock.method(globalThis,'requestAnimationFrame',callback=>{frames.set(++next,callback);return next;});
    t.mock.method(globalThis,'cancelAnimationFrame',id=>frames.delete(id));
    const peers=new SketchPresence(dom.root);t.after(()=>peers.dispose());
    return {peers,frames,reduced,step(time){now=time;for(const [id,callback]of [...frames]){frames.delete(id);callback(time);}}};
}
const plainView={pan:[0,0],zoom:1,angle:0};
const peerAt=(x,y=0)=>[[1,{user,pointer:{x,y,tool:'pen',width:8}}]];
test('remote sketch cursors interpolate, retarget continuously and stop after the last update',t=>{
    const {peers,frames,step}=animatedPresence(t);
    peers.render(peerAt(0),plainView);assert.equal(frames.size,0);
    const entry=peers.markers.get(1);peers.render(peerAt(100),plainView);
    assert.deepEqual(entry.point,[0,0]);assert.equal(frames.size,1);
    step(40);assert.deepEqual(entry.point,[75,0]);
    peers.render(peerAt(200),plainView);assert.deepEqual(entry.point,[75,0]);assert.equal(frames.size,1);
    step(80);assert.deepEqual(entry.point,[168.75,0]);
    step(120);assert.deepEqual(entry.point,[200,0]);assert.equal(frames.size,0);
    assert.equal(entry.root.style.transform,'translate(200px,0px)');
});
test('camera changes apply immediately without restarting cursor interpolation',t=>{
    const {peers,step,frames}=animatedPresence(t);
    peers.render(peerAt(0),plainView);peers.render(peerAt(100),plainView);step(40);
    const entry=peers.markers.get(1),view={pan:[10,20],zoom:2,angle:90};
    peers.render(peerAt(100),view);
    assert.deepEqual(entry.point,[75,0]);assert.equal(entry.ring.style.width,'16px');
    const point=viewportPoint(view,[75,0]);assert.equal(entry.root.style.transform,`translate(${point[0]}px,${point[1]}px)`);
    step(80);assert.deepEqual(entry.point,[100,0]);assert.equal(frames.size,0);
});
test('reduced motion, departed peers and disposal cancel cursor animation',t=>{
    const {peers,step,frames,reduced}=animatedPresence(t);
    peers.render(peerAt(0),plainView);peers.render(peerAt(100),plainView);reduced.matches=true;step(16);
    assert.deepEqual(peers.markers.get(1).point,[100,0]);assert.equal(frames.size,0);
    peers.render(peerAt(200),plainView);assert.deepEqual(peers.markers.get(1).point,[200,0]);assert.equal(frames.size,0);
    reduced.matches=false;peers.render(peerAt(300),plainView);assert.equal(frames.size,1);
    peers.render([],plainView);assert.equal(frames.size,0);assert.equal(peers.markers.size,0);
    peers.render(peerAt(0),plainView);peers.render(peerAt(100),plainView);peers.dispose();assert.equal(frames.size,0);
});

test('the pointer edges are true tangents to the circle forming its concave notch',()=>{
    for(const [tip,center,radius]of [[[2,2],[23,27],11],[[0,0],[30,40],18]]){
        const shape=pointerOutline(tip,center,radius);
        for(const point of shape.tangents){
            const radial=[point[0]-center[0],point[1]-center[1]],edge=[point[0]-tip[0],point[1]-tip[1]];
            assert.ok(Math.abs(Math.hypot(...radial)-radius)<1e-8,'the notch ends must lie on the photo border');
            assert.ok(Math.abs(radial[0]*edge[0]+radial[1]*edge[1])<1e-8,'each arrow side must be perpendicular to the radius at contact');
        }
    }
});
