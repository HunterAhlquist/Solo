import test from 'node:test';import assert from 'node:assert/strict';import {VaultPreferences,viewPreferences} from './preferences.js';
const vault='12345678-1234-1234-1234-123456789012',folder='11111111-1111-1111-1111-111111111111',note='22222222-2222-2222-2222-222222222222';
function fixture(){
    const cookies=new Map(),store=new VaultPreferences(key=>cookies.get(key),(key,value)=>cookies.delete(key));
    store.save=(vault,value)=>{
        const key=store.key(vault),encoded=encodeURIComponent(JSON.stringify(viewPreferences(value))),chunks=encoded.match(/.{1,3000}/g);
        store.clear(vault);chunks.forEach((chunk,i)=>cookies.set(key+'-'+i,chunk));cookies.set(key+'-count',String(chunks.length));
    };
    return {cookies,store};
}
test('fresh vaults start collapsed at normal font size and preferences are isolated per vault',()=>{
    const {store}=fixture();assert.deepEqual(store.load(vault),{openFolders:[],noteId:null,fontScale:100,marginWidth:-1});const value={openFolders:[folder],noteId:note,fontScale:125,marginWidth:10};store.save(vault,value);assert.deepEqual(store.load(vault),value);assert.equal(store.load(folder).noteId,null);
});
test('legacy folder trees load across chunks and clearing removes every chunk',()=>{
    const {store,cookies}=fixture(),openFolders=Array.from({length:300},(_,i)=>'11111111-1111-1111-1111-'+String(i).padStart(12,'0'));
    store.save(vault,{openFolders,noteId:note,fontScale:150});assert.deepEqual(store.load(vault).openFolders,openFolders);assert.ok([...cookies.values()].every(value=>value.length<=3000));
    store.clear(vault);assert.equal(cookies.size,0);assert.deepEqual(store.load(vault).openFolders,[]);
});
test('malformed or stale preference values cannot become markup, routes, or invalid font sizes',()=>{
    const {store,cookies}=fixture();store.save(vault,{openFolders:[folder,folder,'<script>'],noteId:'../other',fontScale:999});assert.deepEqual(store.load(vault),{openFolders:[folder],noteId:null,fontScale:100,marginWidth:-1});
    cookies.set(store.key(vault)+'-0','%broken');assert.deepEqual(store.load(vault),{openFolders:[],noteId:null,fontScale:100,marginWidth:-1});
});
test('margin width round trips with the other preferences and old cookies use Auto',()=>{
    const {store}=fixture();store.save(vault,{openFolders:[folder],noteId:note,fontScale:150,marginWidth:20});assert.equal(store.load(vault).marginWidth,20);
    store.save(vault,{fontScale:125});assert.equal(store.load(vault).marginWidth,-1);store.save(vault,{marginWidth:999});assert.equal(store.load(vault).marginWidth,-1);
});

test('browser preferences survive cookie eviction and are isolated by the desktop origin',async()=>{
    const {loadVaultPreferences,saveVaultPreferences}=await import('./preferences.js');
    const origins=new Map();let origin='http://localhost:41321';
    globalThis.document={cookie:''};globalThis.location={protocol:'http:'};
    globalThis.localStorage={getItem:key=>origins.get(origin)?.get(key)??null,setItem:(key,value)=>{if(!origins.has(origin))origins.set(origin,new Map());origins.get(origin).set(key,value);}};
    const value={openFolders:[folder],noteId:note,fontScale:150,marginWidth:15};
    try{
        saveVaultPreferences(vault,value);document.cookie='';
        assert.deepEqual(loadVaultPreferences(vault),value);
        origin='http://localhost:41322';assert.equal(loadVaultPreferences(vault).fontScale,100);
        origin='http://localhost:41321';assert.deepEqual(loadVaultPreferences(vault),value);
    }finally{delete globalThis.document;delete globalThis.location;delete globalThis.localStorage;}
});

test('migration expires all legacy chunks even when local storage already has the vault',async()=>{
    const {loadVaultPreferences,saveVaultPreferences}=await import('./preferences.js'),{store,cookies}=fixture();
    const openFolders=Array.from({length:300},(_,i)=>'11111111-1111-1111-1111-'+String(i).padStart(12,'0'));
    const value={openFolders,noteId:note,fontScale:150,marginWidth:10};store.save(vault,value);
    const other=folder;store.save(other,{fontScale:125});const persisted=new Map();
    const document={};Object.defineProperty(document,'cookie',{get:()=>[...cookies].map(([k,v])=>k+'='+v).join('; '),set:header=>{const [pair]=header.split(';'),at=pair.indexOf('='),key=pair.slice(0,at);if(header.includes('Max-Age=0'))cookies.delete(key);else cookies.set(key,pair.slice(at+1));}});
    globalThis.document=document;globalThis.location={protocol:'https:'};globalThis.localStorage={getItem:key=>persisted.get(key)??null,setItem:(key,value)=>persisted.set(key,value)};
    try{
        assert.deepEqual(loadVaultPreferences(vault),value);assert.ok(![...cookies.keys()].some(key=>key.startsWith(store.key(vault))));assert.equal(store.load(other).fontScale,125);
        store.save(vault,value);assert.deepEqual(loadVaultPreferences(vault),value);assert.ok(![...cookies.keys()].some(key=>key.startsWith(store.key(vault))));
        localStorage.setItem=()=>{throw new Error('storage unavailable');};saveVaultPreferences(vault,{fontScale:125});assert.ok(![...cookies.keys()].some(key=>key.startsWith(store.key(vault))));
    }finally{delete globalThis.document;delete globalThis.location;delete globalThis.localStorage;}
});
