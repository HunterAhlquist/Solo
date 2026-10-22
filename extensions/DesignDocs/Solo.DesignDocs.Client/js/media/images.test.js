import test from 'node:test';
import assert from 'node:assert/strict';
import { imageAngle } from './images.js';
test('paper angles are stable and bounded per image path',()=>{
    for(let n=0;n<1000;n++){const path=`attachments/${n}.webp`;assert.equal(imageAngle(path),imageAngle(path));assert.ok(Math.abs(imageAngle(path))<=3.5);}
    assert.notEqual(imageAngle('a.webp'),imageAngle('b.webp'));
});
