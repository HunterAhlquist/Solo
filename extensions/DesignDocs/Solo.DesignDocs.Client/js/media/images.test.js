import test from 'node:test';
import assert from 'node:assert/strict';
import { imageAngle,Images,loadingPreview } from './images.js';
const img=src=>({src,alt:'',dataset:{},removeAttribute(name){delete this[name];}});
import { attachmentName } from './vault-paths.js';
test('paper angles are stable and bounded per image path',()=>{
    for(let n=0;n<1000;n++){const path=`attachments/${n}.webp`;assert.equal(imageAngle(path),imageAngle(path));assert.ok(Math.abs(imageAngle(path))<=3.5);}
    assert.notEqual(imageAngle('a.webp'),imageAngle('b.webp'));
});

test('targeted refresh preserves unrelated cache entries, thumbnails, and the open viewer',async t=>{
    const revoked=[];t.mock.method(URL,'revokeObjectURL',url=>revoked.push(url));
    const images=new Images({},true,()=>{},()=>{}),paths=['photo.png','Plan.sketch','Other.sketch'];
    const old=paths.map(path=>Promise.resolve('blob:'+path));paths.forEach((path,i)=>images.cache.set(path,old[i]));
    const thumbnails=paths.map(path=>img('blob:'+path)),refreshed=[];
    images.cards.add({button:{isConnected:true,querySelectorAll:()=>thumbnails},entries:paths.map(path=>({path}))});
    images.resolve=async path=>{refreshed.push(path);return 'new:'+path;};
    let viewerRefreshes=0;images.dialog={open:true};images.refreshViewer=predicate=>{if(!predicate||predicate('photo.png'))viewerRefreshes++;};
    await images.refresh(path=>path==='Plan.sketch');await Promise.resolve();
    assert.deepEqual(refreshed,['Plan.sketch']);assert.deepEqual(revoked,['blob:Plan.sketch']);
    assert.equal(images.cache.get('photo.png'),old[0]);assert.equal(images.cache.get('Other.sketch'),old[2]);
    assert.deepEqual(thumbnails.map(img=>img.src),['blob:photo.png','new:Plan.sketch','blob:Other.sketch']);assert.equal(viewerRefreshes,0);
});

for(const sketch of [false,true])test(`${sketch?'sketch':'image'} removal requires confirmation`,async()=>{
    const entry={path:sketch?'Plan.sketch':'photo.png',sketch},removed=[],calls=[];let approved=null;
    const images=new Images({invokeMethodAsync:async(...args)=>{calls.push(args);return approved;}},true,()=>{},()=>{},{remove:item=>{removed.push(item);return true;}});
    assert.equal(await images.remove(entry),false);assert.deepEqual(removed,[]);
    approved='remove';assert.equal(await images.remove(entry),true);assert.deepEqual(removed,[entry]);
    assert.deepEqual(calls,[['ConfirmRemoveAttachment',entry.path,sketch],['ConfirmRemoveAttachment',entry.path,sketch]]);
});
test('pending confirmation cannot remove attachments after editing is disabled or the editor is disposed',async()=>{
    for(const stop of [images=>{images.canWrite=false;},images=>{images.disposed=true;}]){
        let approve,removed=0;
        const images=new Images({invokeMethodAsync:()=>new Promise(resolve=>{approve=resolve;})},true,()=>{},()=>{},{remove:()=>{removed++;return true;}});
        const pending=images.remove({path:'photo.png'});stop(images);approve('delete');
        assert.equal(await pending,false);assert.equal(removed,0);
    }
});

const deleting=(answers,actions={})=>{
    const calls=[],removed=[],statuses=[];
    const images=new Images({invokeMethodAsync:async(method,...args)=>{calls.push([method,...args]);return answers[method];}},true,()=>{},message=>statuses.push(message),
        {remove:item=>{removed.push(item);return true;},...actions});
    return {images,calls,removed,statuses};
};
for(const sketch of [false,true])test(`deleting a ${sketch?'sketch':'image'} permanently deletes it before removing the embed`,async()=>{
    const entry={path:sketch?'attachments/note/Plan.sketch':'attachments/note/photo.png',sketch},order=[];
    const {images,calls,removed}=deleting({ConfirmRemoveAttachment:'delete',DeleteAttachment:true},{removable:()=>{order.push('check');return true;},remove:item=>{order.push('remove');return true;}});
    const deleteCall=images.bridge.invokeMethodAsync;images.bridge.invokeMethodAsync=async(method,...args)=>{if(method==='DeleteAttachment')order.push('delete');return deleteCall(method,...args);};
    assert.equal(await images.remove(entry),true);
    assert.deepEqual(calls,[['ConfirmRemoveAttachment',entry.path,sketch],['DeleteAttachment',entry.path,sketch]]);
    assert.deepEqual(order,['check','delete','remove']);assert.deepEqual(removed,[]);
});
test('a refused permanent delete leaves the embed in the note',async()=>{
    const {images,calls,removed}=deleting({ConfirmRemoveAttachment:'delete',DeleteAttachment:false});
    assert.equal(await images.remove({path:'photo.png'}),false);assert.deepEqual(removed,[]);
    assert.deepEqual(calls.map(c=>c[0]),['ConfirmRemoveAttachment','DeleteAttachment']);
});
test('an embed that moved elsewhere is not deleted out from under the note',async()=>{
    const {images,calls,removed,statuses}=deleting({ConfirmRemoveAttachment:'delete',DeleteAttachment:true},{removable:()=>false});
    assert.equal(await images.remove({path:'photo.png'}),false);assert.deepEqual(removed,[]);
    assert.deepEqual(calls.map(c=>c[0]),['ConfirmRemoveAttachment']);assert.match(statuses[0],/changed elsewhere/);
});
test('removing only the embed never deletes the attachment',async()=>{
    const {images,calls,removed}=deleting({ConfirmRemoveAttachment:'remove'});
    assert.equal(await images.remove({path:'photo.png'}),true);assert.equal(removed.length,1);
    assert.deepEqual(calls.map(c=>c[0]),['ConfirmRemoveAttachment']);
});
test('an editor closed during a delete does not touch the note',async()=>{
    let finish;const {images,removed}=deleting({ConfirmRemoveAttachment:'delete'});
    const call=images.bridge.invokeMethodAsync;images.bridge.invokeMethodAsync=(method,...args)=>method==='DeleteAttachment'?new Promise(r=>finish=r):call(method,...args);
    const pending=images.remove({path:'photo.png'});await new Promise(r=>setTimeout(r));images.disposed=true;finish(true);
    assert.equal(await pending,false);assert.deepEqual(removed,[]);
});
test('a deleted attachment refreshes its other embeds but not the open viewer',async t=>{
    t.mock.method(URL,'revokeObjectURL',()=>{});
    const {images}=deleting({ConfirmRemoveAttachment:'delete',DeleteAttachment:true});
    const thumbnails=[img('blob:old'),img('blob:other')],resolved=[];
    images.cache.set('photo.png',Promise.resolve('blob:old'));images.cache.set('other.png',Promise.resolve('blob:other'));
    images.cards.add({button:{isConnected:true,querySelectorAll:()=>thumbnails},entries:[{path:'photo.png'},{path:'other.png'}]});
    images.resolve=async path=>{resolved.push(path);throw new Error('Image not found or ambiguous.');};
    let viewerRefreshes=0;images.dialog={open:true};images.refreshViewer=()=>{viewerRefreshes++;};
    assert.equal(await images.remove({path:'photo.png'}),true);await new Promise(r=>setTimeout(r));
    assert.deepEqual(resolved,['photo.png']);assert.equal(images.cache.has('photo.png'),false);assert.equal(images.cache.has('other.png'),true);
    assert.equal(thumbnails[0].alt,'Unavailable: photo.png');assert.equal(viewerRefreshes,0);
});

test('a preview shows a spinner while it loads, then the image',async()=>{
    let finish;const images=new Images({},false,()=>{},()=>{}),thumbnail=img(undefined);
    images.resolve=()=>new Promise(resolve=>finish=resolve);
    images.thumbnail(thumbnail,'attachments/note/Map.sketch');
    assert.equal(thumbnail.src,loadingPreview());assert.equal('loading' in thumbnail.dataset,true);assert.equal(thumbnail.alt,'Loading Map.sketch');
    finish('blob:map');await Promise.resolve();
    assert.equal(thumbnail.src,'blob:map');assert.equal('loading' in thumbnail.dataset,false);assert.equal(thumbnail.alt,'Map.sketch');
});
test('a preview that fails to load shows it is unavailable instead of spinning forever',async()=>{
    const images=new Images({},false,()=>{},()=>{}),thumbnail=img('blob:old');
    images.resolve=async()=>{throw new Error('Sketch not found in this note.');};
    images.thumbnail(thumbnail,'photo.png');await new Promise(r=>setTimeout(r));
    assert.equal('src' in thumbnail,false);assert.equal('loading' in thumbnail.dataset,false);assert.equal(thumbnail.alt,'Unavailable: photo.png');
});
test('a reloading preview spins again, and each image in a stack loads on its own',async()=>{
    const images=new Images({},false,()=>{},()=>{}),thumbnails=[img('blob:a'),img('blob:b')],pending=new Map();
    images.cards.add({button:{isConnected:true,querySelectorAll:()=>thumbnails},entries:[{path:'a.png'},{path:'b.png'}]});
    images.resolve=path=>new Promise(resolve=>pending.set(path,resolve));
    await images.refresh();assert.ok(thumbnails.every(t=>t.src===loadingPreview()));
    pending.get('b.png')('blob:b2');await Promise.resolve();
    assert.equal(thumbnails[0].src,loadingPreview());assert.equal(thumbnails[1].src,'blob:b2');
});
test('the spinner follows the theme colour and holds still for reduced motion',()=>{
    const moving=decodeURIComponent(loadingPreview('#ff0000')),still=decodeURIComponent(loadingPreview('#ff0000',true));
    assert.match(moving,/stroke="#ff0000"/);assert.match(moving,/@keyframes/);
    assert.match(still,/stroke="#ff0000"/);assert.doesNotMatch(still,/animation/);
});

test('attachment display names and downloads exclude the owning note path',()=>{
    assert.equal(attachmentName('attachments/note/Plan.sketch'),'Plan.sketch');
    assert.equal(attachmentName('attachments/note/photo.png'),'photo.png');
    assert.equal(attachmentName('legacy.png'),'legacy.png');
});

test('a slow old thumbnail request cannot overwrite a newer sketch preview',async()=>{
    const images=new Images({},false,()=>{},()=>{}),path='Map.sketch',thumbnail=img('original'),requests=[];
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
