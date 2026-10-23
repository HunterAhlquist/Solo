// Minimal DOM event routing for mountSketch. Rendering and Yjs remain production code;
// only the platform surface is substituted so native-default cancellation is observable.
export class Element {
    constructor(tag='div',ownerDocument){this.tagName=tag.toUpperCase();this.ownerDocument=ownerDocument;this.children=[];this.listeners=new Map();this.style={setProperty(){}};this.className='';this.classList={add:name=>{this.className+=' '+name;}};this.clientWidth=1000;this.clientHeight=800;this.captures=new Set();}
    append(...nodes){for(const node of nodes){node.parentElement=this;this.children.push(node);}}
    replaceChildren(){this.children=[];}
    remove(){this.parentElement.children=this.parentElement.children.filter(child=>child!==this);}
    setAttribute(){} removeAttribute(){}
    matches(selector){return selector.split(',').some(value=>value.trim().startsWith('.')?this.className.split(' ').includes(value.trim().slice(1)):value.trim().toLowerCase()===this.tagName.toLowerCase());}
    closest(selector){return this.matches(selector)?this:this.parentElement?.closest(selector)??null;}
    contains(node){return node===this||this.children.some(child=>child.contains(node));}
    querySelector(selector){for(const child of this.children){if(child.matches(selector))return child;const nested=child.querySelector(selector);if(nested)return nested;}return null;}
    getBoundingClientRect(){return {left:0,top:0,width:this.clientWidth,height:this.clientHeight};}
    focus(){this.ownerDocument.activeElement=this;} blur(){if(this.ownerDocument.activeElement===this)this.ownerDocument.activeElement=null;this.emit('blur');}
    setPointerCapture(id){this.captures.add(id);} hasPointerCapture(id){return this.captures.has(id);} releasePointerCapture(id){this.captures.delete(id);}
    addEventListener(type,handler,options={}){const listeners=this.listeners.get(type)??[];listeners.push({handler,capture:options===true||!!options.capture,passive:!!options.passive});this.listeners.set(type,listeners);}
    removeEventListener(type,handler){this.listeners.set(type,(this.listeners.get(type)??[]).filter(item=>item.handler!==handler));}
    emit(type,values={}){
        const event={type,target:this,cancelable:true,defaultPrevented:false,preventDefault(){if(this.cancelable&&!this.passive)this.defaultPrevented=true;},stopPropagation(){this.stopped=true;},...values};
        const path=[];for(let node=this;node;node=node.parentElement)path.push(node);
        for(const [nodes,capture] of [[path.toReversed(),true],[path,false]])for(const node of nodes){
            for(const item of node.listeners.get(type)??[])if(item.capture===capture){event.passive=item.passive;item.handler(event);}if(event.stopped)return event;
        }return event;
    }
    getContext(){return this.context??={canvas:this,clearRect(){},fillRect(){},drawImage(){},measureText:text=>({width:text.length*8}),fillText(){},strokeRect(){},setLineDash(){},beginPath(){},moveTo(){},lineTo(){},closePath(){},stroke(){}};}
}
export function installDOM(){
    const restore=[];
    const document=new Element('document');document.ownerDocument=document;document.fonts={ready:Promise.resolve()};document.hidden=false;
    document.createElement=tag=>new Element(tag,document);document.body=document.createElement('body');document.head=document.createElement('head');document.append(document.body,document.head);
    const selection={rangeCount:0,anchorNode:null,focusNode:null,removeAllRanges(){this.rangeCount=0;this.anchorNode=this.focusNode=null;}};document.getSelection=()=>selection;
    const window=new Element('window',document);document.defaultView=window;window.getSelection=()=>selection;
    let frame=0;const frames=new Map();
    const values={document,window,getComputedStyle:()=>({color:'#111',fontFamily:'sans-serif',getPropertyValue:()=> 'white'}),Image:class extends Element {constructor(){super('img',document);}},Audio:class {pause(){} removeAttribute(){}},MutationObserver:class{observe(){}disconnect(){}},IntersectionObserver:class{observe(){}disconnect(){}},ResizeObserver:class{observe(){}disconnect(){}},matchMedia:()=>({matches:true}),requestAnimationFrame:callback=>{frames.set(++frame,callback);return frame;},cancelAnimationFrame:id=>frames.delete(id),fetch:async()=>({ok:false})};
    for(const [key,value]of Object.entries(values)){const before=Object.getOwnPropertyDescriptor(globalThis,key);Object.defineProperty(globalThis,key,{value,writable:true,configurable:true});restore.push(()=>{if(before)Object.defineProperty(globalThis,key,before);else delete globalThis[key];});}
    const dialog=document.createElement('div');dialog.className='design-docs-sketch-dialog';const shell=document.createElement('div');shell.className='design-docs-sketch-shell';const root=document.createElement('div');document.body.append(dialog);dialog.append(shell);shell.append(root);
    return {document,root,dialog,selection,restore(){for(const action of restore)action();},flushFrames(){for(const [id,callback]of frames){frames.delete(id);callback();}}};
}
