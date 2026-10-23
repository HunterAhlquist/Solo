import test from 'node:test';import assert from 'node:assert/strict';
import {caretPalettePosition} from './palette.js';
const body={left:100,top:50,right:900,bottom:650},viewport={left:100,top:100,right:880,bottom:650},size={width:56,height:152};
test('palette follows the current line at the text column margin regardless of caret column',()=>{
    assert.deepEqual(caretPalettePosition({left:300,top:200,bottom:224},body,viewport,size,240),{left:76,top:134});
    assert.deepEqual(caretPalettePosition({left:700,top:260,bottom:284},body,viewport,size,240),{left:76,top:194});
});
test('palette stays reachable at viewport edges and hides with an offscreen line',()=>{
    assert.deepEqual(caretPalettePosition({left:870,top:620,bottom:644},body,viewport,size,140),{left:8,top:440});
    assert.equal(caretPalettePosition({left:300,top:60,bottom:84},body,viewport,size,240),null);
    assert.equal(caretPalettePosition(null,body,viewport,size,240),null);
});
