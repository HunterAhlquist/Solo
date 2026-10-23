import test from 'node:test';import assert from 'node:assert/strict';
import {selectPolygon,selectionBounds,shiftRegion} from './selection.js';
import {rasterize,splitSelection} from './render.js';
const stroke=(id,y)=>({id,tool:'pen',ink:'theme',level:0,pointerType:'mouse',seed:27,points:[[5,y,1],[30,y,1]]});
test('box selection collects multiple disconnected strokes and text, excluding outside work',()=>{
    const raster=rasterize([stroke('a',10),stroke('b',20),stroke('c',35)],50,50);
    const result=selectPolygon(raster,50,50,[[0,0],[40,0],[40,28],[0,28]],[{id:'text',bounds:{left:2,top:2,right:15,bottom:8}}]);
    assert.deepEqual([...result.region.owners].sort(),[1,2]);assert.deepEqual([...result.textIds],['text']);
    const bounds=selectionBounds(result.region,50);assert.ok(bounds.top<12&&bounds.bottom>19&&bounds.bottom<28);
});
test('lasso selection follows its polygon rather than the bounding rectangle',()=>{
    const raster={pixels:new Uint8Array(100),owners:new Int32Array(100)};for(const p of [11,18,81,88]){raster.pixels[p]=1;raster.owners[p]=p;}
    const result=selectPolygon(raster,10,10,[[0,0],[10,0],[0,10]]);assert.equal(result.region.mask[11],1);assert.equal(result.region.mask[88],0);
});
test('drag preview moves the selected fragments before committing and preserves unrelated strokes',()=>{
    const original=[stroke('a',10),stroke('b',35)],raster=rasterize(original,50,50),selection=selectPolygon(raster,50,50,[[0,0],[40,0],[40,20],[0,20]]);
    const parts=splitSelection(original,selection.region,50,50,0,12,new Set(['a']));assert.ok(parts.moved.length);assert.equal(parts.stationary.find(s=>s.id==='b'),original[1]);
    const next=rasterize(parts.stationary.concat(parts.moved),50,50);assert.equal(next.pixels[10*50+15],0);assert.ok([21,22,23].some(y=>next.pixels.slice(y*50+10,y*50+20).some(Boolean)));assert.equal(original[0].points[0][1],10);
    const moved=shiftRegion(selection.region,50,50,0,12);assert.equal(moved.mask[10*50+15],0);assert.ok([21,22,23].some(y=>moved.mask.slice(y*50+10,y*50+20).some(Boolean)));
});
