import test from 'node:test';import assert from 'node:assert/strict';import {VaultPreferences} from './preferences.js';
const vault='12345678-1234-1234-1234-123456789012',folder='11111111-1111-1111-1111-111111111111',note='22222222-2222-2222-2222-222222222222';
function fixture(){const cookies=new Map(),store=new VaultPreferences(key=>cookies.get(key),(key,value)=>value===null?cookies.delete(key):cookies.set(key,value));return {cookies,store};}
test('fresh vaults start collapsed at normal font size and preferences are isolated per vault',()=>{
    const {store}=fixture();assert.deepEqual(store.load(vault),{openFolders:[],noteId:null,fontScale:100});const value={openFolders:[folder],noteId:note,fontScale:125};store.save(vault,value);assert.deepEqual(store.load(vault),value);assert.equal(store.load(folder).noteId,null);
});
test('a large folder tree survives cookie chunking and shrinking removes old chunks',()=>{
    const {store,cookies}=fixture(),openFolders=Array.from({length:300},(_,i)=>'11111111-1111-1111-1111-'+String(i).padStart(12,'0'));
    store.save(vault,{openFolders,noteId:note,fontScale:150});assert.deepEqual(store.load(vault).openFolders,openFolders);assert.ok([...cookies.values()].every(value=>value.length<=3000));
    store.save(vault,{openFolders:[],fontScale:100});assert.equal(cookies.size,2);assert.deepEqual(store.load(vault).openFolders,[]);
});
test('malformed or stale preference values cannot become markup, routes, or invalid font sizes',()=>{
    const {store,cookies}=fixture();store.save(vault,{openFolders:[folder,folder,'<script>'],noteId:'../other',fontScale:999});assert.deepEqual(store.load(vault),{openFolders:[folder],noteId:null,fontScale:100});
    cookies.set(store.key(vault)+'-0','%broken');assert.deepEqual(store.load(vault),{openFolders:[],noteId:null,fontScale:100});
});
