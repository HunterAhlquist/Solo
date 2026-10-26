import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

const source=fs.readFileSync(new URL('../src/Solo.Client/wwwroot/js/solo-interop.js',import.meta.url),'utf8');
function page(){
    const listeners=new Map(),dataset={};
    vm.runInNewContext(source,{
        window:{matchMedia:()=>({matches:false})},
        document:{documentElement:{dataset},addEventListener:(type,listener)=>listeners.set(type,listener)}
    });
    return {dataset,emit:(type,event)=>listeners.get(type)?.(event)};
}
test('a trackpad enables tooltips even when media queries report no hover',()=>{
    const p=page();p.emit('pointerover',{pointerType:'mouse'});
    assert.equal(p.dataset.soloInput,'mouse');
});
test('touch and Pencil suppress tooltips, and trackpad movement restores them',()=>{
    const p=page();
    for(const pointerType of ['touch','pen']){
        p.emit('pointerdown',{pointerType});assert.equal(p.dataset.soloInput,pointerType);
        p.emit('pointermove',{pointerType:'mouse',buttons:0});assert.equal(p.dataset.soloInput,'mouse');
    }
});
test('keyboard focus can show names without enabling tooltips for touch',()=>{
    const p=page();p.emit('pointerdown',{pointerType:'touch'});
    p.emit('keydown',{key:'Tab'});assert.equal(p.dataset.soloInput,'keyboard');
    p.emit('pointerdown',{pointerType:'pen'});assert.equal(p.dataset.soloInput,'pen');
});
