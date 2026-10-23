import test from 'node:test';import assert from 'node:assert/strict';import {mountTitleTicker} from './tree-ticker.js';
function fixture(){
    const listeners=new Map(),media={matches:false};let blocked=false;
    const previous=globalThis.window;globalThis.window={matchMedia:()=>media};
    const label=width=>({dataset:{},clientWidth:150,style:{setProperty(name,value){this[name]=value;}},querySelector:()=>({getBoundingClientRect:()=>({width})})});
    const root={contains:()=>true,addEventListener:(name,callback)=>listeners.set(name,callback),removeEventListener:name=>listeners.delete(name)};
    const ticker=mountTitleTicker(root,()=>blocked);
    return {label,media,ticker,listeners,block:()=>blocked=true,over:(title,pointerType='mouse')=>listeners.get('pointerover')({target:{closest:()=>title},pointerType}),restore(){ticker.dispose();globalThis.window=previous;}};
}
test('overflowing titles use the Roadmaps speed and stop when leaving or disposing',()=>{
    const f=fixture();try{const label=f.label(450);f.over(label);assert.equal(label.dataset.ticker,'');assert.equal(label.style['--design-docs-ticker-distance'],'450px');assert.equal(label.style['--design-docs-ticker-duration'],'10s');f.listeners.get('pointerleave')();assert.equal(label.dataset.ticker,undefined);f.over(label);f.ticker.dispose();assert.equal(label.dataset.ticker,undefined);assert.equal(f.listeners.size,0);}finally{f.restore();}
});
test('a fitting label does not tick, and switching to it stops the previous title',()=>{
    const f=fixture();try{const long=f.label(450),short=f.label(100);f.over(long);f.over(short);assert.equal(long.dataset.ticker,undefined);assert.equal(short.dataset.ticker,undefined);}finally{f.restore();}
});
test('touch, active drags, and reduced motion leave labels still',()=>{
    const f=fixture();try{const title=f.label(450);f.over(title,'touch');assert.equal(title.dataset.ticker,undefined);f.media.matches=true;f.over(title);assert.equal(title.dataset.ticker,undefined);f.media.matches=false;f.block();f.over(title);assert.equal(title.dataset.ticker,undefined);}finally{f.restore();}
});
