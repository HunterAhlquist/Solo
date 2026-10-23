import test from 'node:test';import assert from 'node:assert/strict';
import {caretPalettePosition} from './palette.js';
const body={left:100,top:50,right:900,bottom:650},viewport={left:100,top:100,right:880,bottom:650},size={width:128,height:48};
test('palette follows the caret below the text in note coordinates',()=>{
    assert.deepEqual(caretPalettePosition({left:300,top:200,bottom:224},body,viewport,size),{left:200,top:182});
    assert.deepEqual(caretPalettePosition({left:430,top:260,bottom:284},body,viewport,size),{left:330,top:242});
});
test('palette stays reachable at the viewport edges and hides with an offscreen caret',()=>{
    assert.deepEqual(caretPalettePosition({left:870,top:620,bottom:644},body,viewport,size),{left:644,top:544});
    assert.equal(caretPalettePosition({left:300,top:60,bottom:84},body,viewport,size),null);
    assert.equal(caretPalettePosition(null,body,viewport,size),null);
});
