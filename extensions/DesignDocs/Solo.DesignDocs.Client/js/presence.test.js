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
