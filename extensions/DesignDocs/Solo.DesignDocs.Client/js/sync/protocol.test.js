import test from 'node:test';
import assert from 'node:assert/strict';
import { unpackUpdates } from './protocol.js';

test('binary replay batches validate lengths before reading updates',()=>{
    assert.deepEqual(unpackUpdates(new Uint8Array([1,0,0,0,2,0,0,0,7,8])),[new Uint8Array([7,8])]);
    for(const bytes of [[],[1,0,0,0],[255,255,255,255],[1,0,0,0,0,0,0,0],[1,0,0,0,3,0,0,0,7,8],[0,0,0,0,1]])
        assert.throws(()=>unpackUpdates(new Uint8Array(bytes)));
});
