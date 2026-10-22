import test from 'node:test';import assert from 'node:assert/strict';
import {pressureWidth,jitter,rasterize,connectedRegion,splitMove,pageSize,shape} from './render.js';
const stroke={id:'a',tool:'pen',ink:'theme',level:0,pointerType:'mouse',seed:27,points:[[5,20,1],[55,20,1]]};
test('pressure rises from the selected minimum and mouse ignores it',()=>{assert.equal(pressureWidth(0,0,'pen'),1);assert.equal(pressureWidth(0,1,'pen'),8);assert.equal(pressureWidth(3,0,'pen'),5);assert.equal(pressureWidth(3,1,'mouse'),5);});
test('seeded raster frames repeat deterministically and jitter stays within one pixel',()=>{assert.deepEqual(rasterize([stroke],64,40),rasterize([stroke],64,40));for(let i=0;i<100;i++)assert.ok(jitter(27,i,2).every(n=>Math.abs(n)<=1));});
test('erasers cut visible ink and moving one cut component preserves the other',()=>{
    const thick={...stroke,level:2};const eraser={...stroke,id:'eraser',tool:'eraser',level:4,points:[[30,0,1],[30,39,1]]};
    const original=rasterize([thick,eraser],64,40),region=connectedRegion(original,64,40,10,20);assert.ok(region);assert.equal(original.pixels[20*64+30],0);
    const moved=splitMove([thick,eraser],region,64,40,0,10);assert.ok(moved.length>2);const raster=rasterize(moved,64,40);assert.ok(raster.pixels[20*64+50]);assert.ok(raster.pixels[30*64+10]);
});
test('paper dimensions and constrained shapes use paint pixels',()=>{assert.deepEqual(pageSize({pageSize:'A4',orientation:'portrait'}),[595,842]);assert.deepEqual(pageSize({pageSize:'A5',orientation:'landscape'}),[595,420]);const rect=shape('rectangle',[0,0],[20,10],true);assert.deepEqual(rect[2],[20,20,1]);});
test('malformed and extreme stroke samples have a bounded rendering cost',()=>{
    assert.doesNotThrow(()=>rasterize([{...stroke,points:[[NaN,0,1],[Infinity,0,1]]}],32,32));
    const output=rasterize([{...stroke,points:[[-1e12,0,1],[1e12,0,1]]}],32,32);assert.equal(output.pixels.length,1024);
});
test('the thinnest wiggled diagonal remains one selectable component',()=>{
    const line={...stroke,points:[[2,2,1],[50,35,1]]},raster=rasterize([line],64,40),first=raster.pixels.findIndex(p=>p>0);
    const region=connectedRegion(raster,64,40,first%64,Math.floor(first/64));assert.equal(region.mask.reduce((a,b)=>a+b,0),raster.pixels.reduce((a,b)=>a+(b>0),0));
});
test('moving a thin cut piece leaves no original jitter fragments behind',()=>{
    const eraser={...stroke,id:'eraser',tool:'eraser',level:4,points:[[30,0,1],[30,39,1]]},original=rasterize([stroke,eraser],64,40,0);
    const region=connectedRegion(original,64,40,10,20),moved=rasterize(splitMove([stroke,eraser],region,64,40,0,10),64,40,0);
    for(let i=0;i<region.mask.length;i++)if(region.mask[i])assert.equal(moved.pixels[i],0,`original pixel ${i} remained`);
});
