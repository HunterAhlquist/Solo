import test from 'node:test';
import assert from 'node:assert/strict';
import { imageAngle,Images } from './images.js';
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
