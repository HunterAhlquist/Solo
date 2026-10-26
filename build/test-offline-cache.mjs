import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs';import vm from 'node:vm';

const hostSource=fs.readFileSync(new URL('../src/Solo.Client/wwwroot/js/offline.js',import.meta.url),'utf8');
function host(fetch){
    const window={solo:{},addEventListener(){}},navigator={onLine:true};
    vm.runInNewContext(hostSource,{window,navigator,indexedDB:{open:()=>({})},fetch,AbortController,setTimeout,clearTimeout});
    return {offline:window.solo.offline,navigator};
}
test('a failed request does not permanently prevent checking for a recovered server',async()=>{
    let requests=0;const page=host(async(path,options)=>{requests++;assert.equal(path,'/api/config/auth');assert.equal(options.cache,'no-store');return {ok:true};});
    page.offline.network(false);assert.equal(page.offline.isOffline(),true);
    assert.equal(await page.offline.reconnect(),true);assert.equal(page.offline.isOffline(),false);assert.equal(requests,1);
    page.navigator.onLine=false;assert.equal(await page.offline.reconnect(),false);assert.equal(requests,1);
});
test('failed connectivity probes keep offline caches available',async()=>{
    const page=host(async()=>{throw new TypeError('Unavailable');});page.offline.network(false);
    assert.equal(await page.offline.reconnect(),false);assert.equal(page.offline.isOffline(),true);
});

const workerSource=fs.readFileSync(new URL('../src/Solo.Client/wwwroot/service-worker.js',import.meta.url),'utf8');
function worker(){
    const origin='https://solo.test',listeners=new Map(),entries=new Map(),writes=[];let online=true,version='new';
    const href=request=>new URL(typeof request==='string'?request:request.url,origin+'/').href;
    const cache={match:async request=>entries.get(href(request))?.clone(),put:async(request,response)=>{entries.set(href(request),response);writes.push(href(request));},keys:async request=>[...entries.keys()].filter(url=>!request||new URL(url).pathname===new URL(href(request)).pathname).map(url=>new Request(url)),delete:async request=>entries.delete(href(request))};
    vm.runInNewContext(workerSource,{URL,Request,importScripts(){},self:{importScripts(){},assetsManifest:{version:'test',assets:[]},location:{origin},addEventListener:(type,handler)=>listeners.set(type,handler)},caches:{open:async name=>name==='solo-offline-assets'?cache:{match:async()=>null}},fetch:async request=>{if(!online)throw new TypeError('Offline');return new Response(version+':'+href(request));}});
    return {entries,writes,setOnline(value){online=value;},setVersion(value){version=value;},async get(path){let result;const pending=[];listeners.get('fetch')({request:new Request(new URL(path,origin)),respondWith:value=>result=value,waitUntil:value=>pending.push(value)});const response=await result;await Promise.all(pending);return response;}};
}
test('unversioned extension content refreshes online and remains available offline',async()=>{
    const p=worker(),url='https://solo.test/api/extensions/design-docs/content/spelling-worker.js';p.entries.set(url,new Response('old'));
    assert.match(await (await p.get(url)).text(),/^new:/);p.setOnline(false);assert.match(await (await p.get(url)).text(),/^new:/);
});
test('versioned assets reuse their cache and discard superseded versions of the same asset',async()=>{
    const p=worker(),url='https://solo.test/api/extensions/design-docs/content/design-docs.js';
    p.entries.set(url+'?v=older',new Response('older'));p.entries.set(url+'?v=old',new Response('old'));p.entries.set(url.replace('design-docs.js','design-docs.css')+'?v=old',new Response('other asset'));
    assert.match(await (await p.get(url+'?v=new')).text(),/^new:/);assert.equal(p.entries.has(url+'?v=old'),true);assert.equal(p.entries.has(url+'?v=older'),false);
    assert.equal(p.entries.size,3);p.setVersion('unexpected');assert.match(await (await p.get(url+'?v=new')).text(),/^new:/);
});
test('cached hashed theme assets retain only two revisions of each file',async()=>{
    const p=worker(),url='https://solo.test/api/themes/quest/';
    for(const hash of ['older','old','current'])p.entries.set(url+hash+'/font.woff2',new Response(hash));
    p.entries.set(url+'older/background.png',new Response('background'));p.setOnline(false);
    assert.equal(await (await p.get(url+'current/font.woff2')).text(),'current');assert.equal(p.entries.has(url+'older/font.woff2'),false);assert.equal(p.entries.size,3);
});
