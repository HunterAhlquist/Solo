import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { EventEmitter } from 'node:events';

const require=createRequire(import.meta.url),root=path.dirname(require.resolve('../main.js'));
const ipc=new EventEmitter();ipc.invoke=async()=>false;
const renderer=new EventTarget();const received=[];
renderer.addEventListener('solo:rotate-gesture',event=>received.push(event.detail));
class GestureEvent extends Event {constructor(name,options){super(name);this.detail=options.detail;}}
vm.runInNewContext(fs.readFileSync(path.join(root,'preload/desktop.js'),'utf8'),{
    require:()=>({contextBridge:{exposeInMainWorld(){}},ipcRenderer:ipc}),
    process:{platform:'darwin',argv:[]},window:renderer,CustomEvent:GestureEvent,
    document:{documentElement:{classList:{add(){},toggle(){}},style:{setProperty(){}}},addEventListener(){}},
},{filename:'preload/desktop.js'});
let destroyed=false;
class Window extends EventEmitter {
    constructor(){super();this.webContents=Object.assign(new EventEmitter(),{
        send:(channel,payload)=>ipc.emit(channel,{},payload),setWindowOpenHandler(){},isDestroyed:()=>destroyed,getZoomFactor:()=>2,
    });}
    getContentBounds(){return {x:80,y:50,width:1440,height:920};}
    isFocused(){return true;}
    setTitle(){}
    loadURL(){}
}
const app=Object.assign(new EventEmitter(),{getName:()=> 'Solo',userAgentFallback:'Solo Electron/44',requestSingleInstanceLock:()=>true,whenReady:()=>new Promise(()=>{})});
const scope=vm.createContext({
    require:id=>id==='electron'?{app,BrowserWindow:Window,Menu:{setApplicationMenu(){}},screen:{getCursorScreenPoint:()=>({x:580,y:450})}}:
        id==='./main/app-identity'?{configureAppIdentity(){}}:
        id==='./main/settings'?{save(){}}:
        id==='./main/menu'?{buildMenu(){}}:
        id==='./main/context-menu'?{attachContextMenu(){}}:
        id.startsWith('./main/')?{}:require(id),
    process:{env:{},platform:'darwin',on(){}},__dirname:root,console,
});
vm.runInContext(fs.readFileSync(path.join(root,'main.js'),'utf8'),scope,{filename:'main.js'});
const win=vm.runInContext("settings={nativeEffects:false,instances:[{id:'fixture',name:'Fixture',port:41321}]};createInstanceWindow(settings.instances[0])",scope);
win.emit('rotate-gesture',{},12.5);
assert.equal(received.length,1,'A native trackpad rotation must reach the renderer');
assert.equal(received[0].rotation,12.5);
assert.deepEqual(Object.keys(received[0]).sort(),['clientX','clientY','rotation']);
assert.equal(received[0].clientX,250);assert.equal(received[0].clientY,200);
win.emit('rotate-gesture',{},0);assert.equal(received.at(-1).rotation,0,'Keep the native end marker');
win.emit('rotate-gesture',{},NaN);assert.equal(received.length,2,'Invalid angles must not cross the bridge');
destroyed=true;win.emit('rotate-gesture',{},3);assert.equal(received.length,2);
console.log('PASS: real instance-window registration and preload deliver native rotation deltas/end markers with zoom-adjusted cursor coordinates.');
