import test from 'node:test';
import assert from 'node:assert/strict';
import { imageAngle,Images } from './images.js';
import { attachmentName } from './vault-paths.js';
test('paper angles are stable and bounded per image path',()=>{
    for(let n=0;n<1000;n++){const path=`attachments/${n}.webp`;assert.equal(imageAngle(path),imageAngle(path));assert.ok(Math.abs(imageAngle(path))<=3.5);}
    assert.notEqual(imageAngle('a.webp'),imageAngle('b.webp'));
});

test('targeted refresh preserves unrelated cache entries, thumbnails, and the open viewer',async t=>{
    const revoked=[];t.mock.method(URL,'revokeObjectURL',url=>revoked.push(url));
    const images=new Images({},true,()=>{},()=>{}),paths=['photo.png','Plan.sketch','Other.sketch'];
    const old=paths.map(path=>Promise.resolve('blob:'+path));paths.forEach((path,i)=>images.cache.set(path,old[i]));
    const thumbnails=paths.map(path=>({src:'blob:'+path})),refreshed=[];
    images.cards.add({button:{isConnected:true,querySelectorAll:()=>thumbnails},entries:paths.map(path=>({path}))});
    images.resolve=async path=>{refreshed.push(path);return 'new:'+path;};
    let viewerRefreshes=0;images.dialog={open:true};images.refreshViewer=predicate=>{if(!predicate||predicate('photo.png'))viewerRefreshes++;};
    await images.refresh(path=>path==='Plan.sketch');await Promise.resolve();
    assert.deepEqual(refreshed,['Plan.sketch']);assert.deepEqual(revoked,['blob:Plan.sketch']);
    assert.equal(images.cache.get('photo.png'),old[0]);assert.equal(images.cache.get('Other.sketch'),old[2]);
    assert.deepEqual(thumbnails.map(img=>img.src),['blob:photo.png','new:Plan.sketch','blob:Other.sketch']);assert.equal(viewerRefreshes,0);
});

for(const sketch of [false,true])test(`${sketch?'sketch':'image'} removal requires confirmation`,async()=>{
    const entry={path:sketch?'Plan.sketch':'photo.png',sketch},removed=[],calls=[];let approved=false;
    const images=new Images({invokeMethodAsync:async(...args)=>{calls.push(args);return approved;}},true,()=>{},()=>{},{remove:item=>{removed.push(item);return true;}});
    assert.equal(await images.remove(entry),false);assert.deepEqual(removed,[]);
    approved=true;assert.equal(await images.remove(entry),true);assert.deepEqual(removed,[entry]);
    assert.deepEqual(calls,[['ConfirmRemoveAttachment',entry.path,sketch],['ConfirmRemoveAttachment',entry.path,sketch]]);
});
test('pending confirmation cannot remove attachments after editing is disabled or the editor is disposed',async()=>{
    for(const stop of [images=>{images.canWrite=false;},images=>{images.disposed=true;}]){
        let approve,removed=0;
        const images=new Images({invokeMethodAsync:()=>new Promise(resolve=>{approve=resolve;})},true,()=>{},()=>{},{remove:()=>{removed++;return true;}});
        const pending=images.remove({path:'photo.png'});stop(images);approve(true);
        assert.equal(await pending,false);assert.equal(removed,0);
    }
});

test('attachment display names and downloads exclude the owning note path',()=>{
    assert.equal(attachmentName('attachments/note/Plan.sketch'),'Plan.sketch');
    assert.equal(attachmentName('attachments/note/photo.png'),'photo.png');
    assert.equal(attachmentName('legacy.png'),'legacy.png');
});

test('a slow old thumbnail request cannot overwrite a newer sketch preview',async()=>{
    const images=new Images({},false,()=>{},()=>{}),path='Map.sketch',thumbnail={src:'original'},requests=[];
    images.cards.add({button:{isConnected:true,querySelectorAll:()=>[thumbnail]},entries:[{path}]});
    images.resolve=()=>new Promise(resolve=>requests.push(resolve));
    await images.refresh(item=>item===path);await images.refresh(item=>item===path);
    requests[1]('newest');await Promise.resolve();requests[0]('stale');await Promise.resolve();
    assert.equal(thumbnail.src,'newest');
});

test('a failed stale image request cannot remove the replacement cache entry',async t=>{
    const requests=[];t.mock.method(URL,'createObjectURL',()=> 'blob:new');t.mock.method(URL,'revokeObjectURL',()=>{});
    const images=new Images({invokeMethodAsync:()=>new Promise((resolve,reject)=>requests.push({resolve,reject}))},false,()=>{},()=>{});
    const first=images.resolve('photo.png');await images.refresh();const second=images.resolve('photo.png');
    requests[0].reject(new Error('Old request failed'));await assert.rejects(first);
    assert.equal(images.cache.has('photo.png'),true);
    requests[1].resolve({base64:'AA==',contentType:'image/png'});assert.equal(await second,'blob:new');images.dispose();
});
