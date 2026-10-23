import test from 'node:test';import assert from 'node:assert/strict';import {trackpadPressure,applyTrackpadPressure,touchSupported} from './pressure.js';import {pressureWidth,rasterize} from './render.js';
const constants={WEBKIT_FORCE_AT_MOUSE_DOWN:1,WEBKIT_FORCE_AT_FORCE_MOUSE_DOWN:3};
test('Force Touch uses browser thresholds, clamps pressure, and ignores unsupported values',()=>{
    assert.equal(trackpadPressure({webkitForce:2},constants),.5);assert.equal(trackpadPressure({webkitForce:9},constants),1);assert.equal(trackpadPressure({webkitForce:0},constants),0);
    assert.equal(trackpadPressure({},constants),null);assert.equal(trackpadPressure({webkitForce:2},{}),null);assert.equal(trackpadPressure({webkitForce:NaN},constants),null);
});
test('ordinary mice stay fixed-width; confirmed Force Touch strokes use the pen width curve',()=>{
    const stroke={id:'pressure',pointerType:'mouse',tool:'pen',ink:'theme',level:0,seed:27,points:[[5,20,0],[55,20,0]]};
    assert.equal(applyTrackpadPressure(stroke,{webkitForce:1},constants),null);assert.equal(stroke.pointerType,'mouse');assert.equal(pressureWidth(0,1,'mouse'),1);
    const pressure=applyTrackpadPressure(stroke,{webkitForce:3},constants);assert.equal(stroke.pointerType,'trackpad');assert.equal(pressureWidth(0,pressure,stroke.pointerType),8);
    const thin=rasterize([stroke],64,40).pixels.filter(Boolean).length;stroke.points=stroke.points.map(([x,y])=>[x,y,pressure]);assert.ok(rasterize([stroke],64,40).pixels.filter(Boolean).length>thin);
    assert.equal(applyTrackpadPressure({pointerType:'pen'},{webkitForce:3},constants),null);
});
test('touch detection checks actual touch contacts rather than mobile platform names',()=>{assert.equal(touchSupported({maxTouchPoints:5}),true);assert.equal(touchSupported({maxTouchPoints:0}),false);assert.equal(touchSupported({}),false);});
