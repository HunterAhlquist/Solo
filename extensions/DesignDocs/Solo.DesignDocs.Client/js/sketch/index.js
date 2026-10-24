import { presenceTheme,SketchPresence } from '../presence.js';
import { bindDesktopRotation } from './rotation.js';
import * as Y from 'yjs';
import { Provider } from '../sync/provider.js';
import { pageSize,paint,connectedRegion,splitSelection,shape,pressureWidth } from './render.js';
import { loadFont,textSize } from './font.js';
import { pagePoint,anchoredView,wheelView,touchView } from './viewport.js';
import { contains,selectPolygon,selectionBounds,shiftRegion } from './selection.js';
import { SketchScene } from './scene.js';
import { SketchInput } from './input.js';
import { applyTrackpadPressure } from './pressure.js';
import { strokePresence } from './stroke-presence.js';
const local=Symbol('local sketch');
export async function mountSketch(root,bridge,options){
    const font=await loadFont(root),shell=root.closest('.design-docs-sketch-shell')??root;
    let frameRequest=null,active=null,selected=null,marquee=null,editing=null,frame=0,disposed=false,visible=true;
    let pan=[12,12],zoom=1,angle=0,gesture=null,dragPan=null,space=false,safariGesture=null,fitPending=true;
    let mousePressure=0,lastPointer=null,lastPointerType='mouse';
    let tool='pen',ink='theme',level=0,constrain=false,clearing=false;
    const touches=new Map(),downloads=[],timers=new Set();
    const viewport=document.createElement('div');viewport.className='design-docs-sketch-viewport';viewport.tabIndex=0;
    viewport.setAttribute('aria-label','Drawing view. Scroll to pan; Ctrl or Command and scroll to zoom; Space and drag to pan.');
    const canvas=document.createElement('canvas');canvas.setAttribute('aria-label','Collaborative sketch');canvas.style.transformOrigin='0 0';const context=canvas.getContext('2d'),scene=new SketchScene(()=>document.createElement('canvas')),inputMode=new SketchInput();
    const selectionCanvas=document.createElement('canvas');selectionCanvas.className='design-docs-sketch-selection';selectionCanvas.setAttribute('aria-hidden','true');selectionCanvas.style.transformOrigin='0 0';selectionCanvas.hidden=true;const selectionContext=selectionCanvas.getContext('2d');let selectionPadding=0;
    const status=document.createElement('output');status.className='design-docs-sketch-status';status.setAttribute('aria-live','polite');
    root.classList.add('design-docs-sketch-editor');root.append(viewport,status);viewport.append(canvas,selectionCanvas);
    const peerCursors=new SketchPresence(viewport);
    const explosion=new Image();explosion.className='design-docs-clear-explosion';explosion.alt='';explosion.hidden=true;viewport.append(explosion);
    const effectReady=fetch(options.explosionUrl).then(response=>response.ok?response.blob():null).catch(()=>null);
    const sound=new Audio(options.explosionSoundUrl);sound.preload='auto';
    const reduced=matchMedia('(prefers-reduced-motion: reduce)');
    const say=message=>{status.textContent=message;};
    const provider=new Provider({send:(kind,data)=>bridge.invokeMethodAsync('Send',kind,data)}, {canWrite:options.canWrite,user:options.user,atomicSync:true,
        status:message=>{say(message);if(provider.synced&&provider.canWrite&&!meta.size)provider.doc.transact(()=>Object.entries(options.meta).forEach(([k,v])=>meta.set(k,v)),local);},
        writable:value=>{bridge.invokeMethodAsync('Writable',value);if(!value){active=null;selected=null;marquee=null;viewport.querySelector('textarea')?.blur();requestDraw();}},
    });
    const ops=provider.doc.getArray('ops'),texts=provider.doc.getMap('texts'),meta=provider.doc.getMap('meta');
    const undo=new Y.UndoManager([ops,texts,meta],{trackedOrigins:new Set([local])});
    const getMeta=()=>({...options.meta,...meta.toJSON()});
    const getTexts=()=>[...texts].map(([id,value])=>({id,...value.toJSON()}));
    let inkColor,fontFamily,paperColor,themeKey;
    const themeInk=()=>inkColor;
    function colors(){const style=getComputedStyle(root),key=[style.color,style.fontFamily,style.getPropertyValue('--mud-palette-surface')].join('|');
        if(key===themeKey)return;themeKey=key;inkColor=style.color;fontFamily=style.fontFamily;paperColor=style.getPropertyValue('--mud-palette-surface')||'white';
        presenceTheme(root);shell.style.setProperty('--design-docs-ink-theme',inkColor);scene.setStyle({themeInk:inkColor,paperColor});
    }
    colors();
    const themeObserver=new MutationObserver(()=>{const before=themeKey;colors();if(before!==themeKey)requestDraw();});
    themeObserver.observe(document.head,{childList:true,subtree:true,characterData:true});
    themeObserver.observe(document.body,{attributes:true,attributeFilter:['class','style']});
    const currentView=()=>({pan,zoom,angle});
    function setView(value){({pan,zoom,angle}=value);fitPending=false;transform();}
    const center=()=>[viewport.clientWidth/2,viewport.clientHeight/2];
    function resetView(){
        const [w,h]=pageSize(getMeta());
        // Fit to the unobstructed rectangle between the floating palettes.
        const left=72,right=20,top=118,bottom=64,availableW=Math.max(40,viewport.clientWidth-left-right),availableH=Math.max(40,viewport.clientHeight-top-bottom);
        zoom=Math.max(.01,Math.min(availableW/w,availableH/h));angle=0;pan=[left+(availableW-w*zoom)/2,top+(availableH-h*zoom)/2];fitPending=false;transform();
    }
    function transform(){
        peerCursors.render([...provider.awareness.getStates()].filter(([id])=>id!==provider.doc.clientID),currentView());
        const [w,h]=pageSize(getMeta()),value=`translate(${pan[0]}px,${pan[1]}px) rotate(${angle}deg) scale(${zoom})`;
        canvas.style.transform=value;explosion.style.width=w+'px';explosion.style.height=h+'px';explosion.style.transform=value;
        selectionCanvas.style.transform=value+` translate(${(selected?.dx??0)-selectionPadding}px,${(selected?.dy??0)-selectionPadding}px)`;
        const input=viewport.querySelector('textarea');
        if(input&&editing){const box={...editing.toJSON(),text:editing.get('text').toString()},bounds=font.bounds(context,box);
            input.style.transform=value+` translate(${box.x}px,${box.y}px)`;input.style.font=`${textSize(box)}px ${fontFamily}`;input.style.lineHeight='1.5';
            input.style.width=Math.max(120,bounds.right-bounds.left+24)+'px';input.style.height=Math.max(textSize(box)*1.5,bounds.bottom-bounds.top)+'px';
        }
    }
    function textBoxes(){return getTexts().map(t=>({...t,bounds:font.bounds(context,t)}));}
    function selection(region,textIds=new Set()){
        const [w]=pageSize(getMeta()),bounds=selectionBounds(region,w,textBoxes().filter(t=>textIds.has(t.id)).map(t=>t.bounds));
        return bounds?{region,textIds,bounds,ids:new Set([...region.owners].map(n=>ops.get(n-1)?.id)),dx:0,dy:0}:null;
    }
    function draw(){
        if(disposed)return;const preview=[];
        if(active)preview.push(active.tool==='rectangle'||active.tool==='ellipse'?{...active,tool:'pen',points:shape(active.tool,active.points[0],active.points.at(-1),constrain||active.shift)}:active);
        const others=[...provider.awareness.getStates()].filter(([id])=>id!==provider.doc.clientID).map(([,state])=>state);
        for(const state of others)if(state.stroke)preview.push(state.stroke);
        const floating=scene.render(context,{texts:getTexts(),extra:preview,selection:selected,selectionContext,font,frame:reduced.matches?0:frame});
        selectionCanvas.hidden=!floating;selectionPadding=floating?.padding??0;
        if(selected){const b=selected.bounds,c=floating?selectionContext:context,dx=floating?selectionPadding:selected.dx,dy=floating?selectionPadding:selected.dy,x=b.left+dx,y=b.top+dy;c.strokeStyle=themeInk();c.lineWidth=1/zoom;c.setLineDash([5/zoom,4/zoom]);c.strokeRect(x-3,y-3,b.right-b.left+6,b.bottom-b.top+6);c.setLineDash([]);
            for(const [cx,cy]of [[x,y],[b.right+dx,y],[x,b.bottom+dy],[b.right+dx,b.bottom+dy]]){c.fillStyle=themeInk();c.fillRect(cx-2/zoom,cy-2/zoom,4/zoom,4/zoom);}}
        if(marquee){const points=marquee.kind==='lasso'?marquee.points:shape('rectangle',marquee.points[0],marquee.points.at(-1));context.strokeStyle=themeInk();context.lineWidth=1/zoom;context.setLineDash([5/zoom,4/zoom]);context.beginPath();points.forEach((p,i)=>i?context.lineTo(...p.slice(0,2)):context.moveTo(...p.slice(0,2)));context.closePath();context.stroke();context.setLineDash([]);}
        transform();
    }
    const requestDraw=()=>{if(frameRequest!==null||disposed)return;frameRequest=requestAnimationFrame(()=>{frameRequest=null;draw();});};
    const sceneChanged=()=>{scene.setScene(ops.toArray(),getMeta());requestDraw();};
    ops.observe(sceneChanged);texts.observeDeep(requestDraw);meta.observe(()=>{sceneChanged();if(fitPending)resetView();});sceneChanged();provider.awareness.on('change',requestDraw);
    const observer=new IntersectionObserver(entries=>{visible=entries[0].isIntersecting;});observer.observe(viewport);
    const boil=setInterval(()=>{if(visible&&!document.hidden&&!disposed&&!reduced.matches&&ops.length){frame=(frame+1)%3;requestDraw();}},250);
    function localPoint(e){if(!Number.isFinite(e.clientX)||!Number.isFinite(e.clientY))return center();const r=viewport.getBoundingClientRect();return [e.clientX-r.left,e.clientY-r.top];}
    function point(e){return [...pagePoint(currentView(),localPoint(e)),e.pointerType==='mouse'?mousePressure:e.pressure];}
    function updateAwareness(p,type=lastPointerType){lastPointer=p;lastPointerType=type;if(!provider.canWrite)return;provider.awareness.setLocalStateField('pointer',{x:p[0],y:p[1],tool,width:pressureWidth(level,p[2]??0,type)});provider.awareness.setLocalStateField('stroke',strokePresence(active));}
    function editText(box){
        viewport.querySelector('textarea')?.blur();editing=box;const text=box.get('text'),input=document.createElement('textarea');input.className='design-docs-sketch-text';input.setAttribute('aria-label','Sketch text');input.value=text.toString();input.spellcheck=false;
        viewport.append(input);transform();undo.stopCapturing();
        input.addEventListener('input',()=>{if(!provider.canWrite)return;const before=text.toString(),after=input.value;let start=0,end=0;while(start<before.length&&start<after.length&&before[start]===after[start])start++;while(end<before.length-start&&end<after.length-start&&before[before.length-1-end]===after[after.length-1-end])end++;
            provider.doc.transact(()=>{text.delete(start,before.length-start-end);text.insert(start,after.slice(start,after.length-end));},local);draw();});
        const reflect=()=>{const before=input.value,after=text.toString();if(before!==after){const cursor=input.selectionStart;input.value=after;input.setSelectionRange(Math.min(after.length,cursor),Math.min(after.length,cursor));}requestDraw();};
        text.observe(reflect);input.addEventListener('blur',()=>{text.unobserve(reflect);input.remove();editing=null;undo.stopCapturing();draw();},{once:true});input.focus();draw();
    }
    function down(e){
        if(e.target.closest('textarea')||e.button>1||clearing)return;
        if(e.pointerType==='mouse')mousePressure=0;
        const mode=inputMode.start(e);if(mode==='ignore'){e.preventDefault();return;}
        if(e.pointerType==='pen'){touches.clear();gesture=null;dragPan=null;if(active?.pointerType==='touch'){active=null;provider.awareness.setLocalStateField('stroke',null);}}
        e.preventDefault();viewport.focus();viewport.setPointerCapture(e.pointerId);
        if(mode==='pan'||e.button===1||space||e.target!==canvas||!provider.canWrite)dragPan={pointer:e.pointerId,point:localPoint(e),pan:[...pan]};
        if(e.pointerType==='touch'){
            touches.set(e.pointerId,localPoint(e));if(touches.size===2){active=null;marquee=null;dragPan=null;selected=null;provider.awareness.setLocalStateField('stroke',null);const [a,b]=[...touches.values()];gesture={pan:[...pan],zoom,angle,mid:[(a[0]+b[0])/2,(a[1]+b[1])/2],distance:Math.hypot(a[0]-b[0],a[1]-b[1]),rotation:Math.atan2(b[1]-a[1],b[0]-a[0])};return;}
        }
        if(dragPan||!provider.canWrite||active||gesture)return;
        const p=point(e);
        if(tool==='select'||tool==='lasso'){
            if(selected&&contains(selected.bounds,p)&&!e.shiftKey){selected.pointer=e.pointerId;selected.start=p;return;}
            selected=null;marquee={kind:tool==='lasso'?'lasso':'rectangle',points:[p],pointer:e.pointerId};draw();return;
        }
        selected=null;
        if(tool==='text'){
            const existing=textBoxes().reverse().find(t=>contains(t.bounds,p));if(existing){editText(texts.get(existing.id));return;}
            const id=crypto.randomUUID(),box=new Y.Map(),text=new Y.Text();undo.stopCapturing();provider.doc.transact(()=>{box.set('x',Math.round(p[0]));box.set('y',Math.round(p[1]));box.set('ink',ink);box.set('scale',2);box.set('text',text);texts.set(id,box);},local);editText(box);return;
        }
        active={id:crypto.randomUUID(),pointerId:e.pointerId,pointerType:e.pointerType,tool,ink,level,seed:Math.floor(Math.random()*2147483647),points:[p],shift:e.shiftKey};undo.stopCapturing();updateAwareness(p,e.pointerType);draw();
    }
    function move(e){
        if(inputMode.ignores(e)){e.preventDefault();return;}
        if(touches.has(e.pointerId))touches.set(e.pointerId,localPoint(e));
        if(gesture&&touches.size>=2){const [a,b]=[...touches.values()];setView(touchView(gesture,a,b));return;}
        if(dragPan?.pointer===e.pointerId){const p=localPoint(e);pan=[dragPan.pan[0]+p[0]-dragPan.point[0],dragPan.pan[1]+p[1]-dragPan.point[1]];fitPending=false;transform();return;}
        const p=point(e);
        if(selected?.pointer===e.pointerId){selected.dx=Math.round(p[0]-selected.start[0]);selected.dy=Math.round(p[1]-selected.start[1]);requestDraw();return;}
        if(marquee?.pointer===e.pointerId){if(marquee.kind==='lasso')marquee.points.push(p);else marquee.points[1]=p;requestDraw();return;}
        if(active?.pointerId===e.pointerId){const samples=e.getCoalescedEvents?.();for(const sample of samples?.length?samples:[e])active.points.push(point(sample));active.shift=e.shiftKey;requestDraw();}
        if(e.pointerType!=='touch')updateAwareness(p,e.pointerType);
    }
    function up(e){
        if(inputMode.finish(e)){touches.delete(e.pointerId);return;}
        touches.delete(e.pointerId);if(gesture){if(touches.size===0)gesture=null;return;}if(dragPan?.pointer===e.pointerId){dragPan=null;return;}
        const [w,h]=pageSize(getMeta());
        if(marquee?.pointer===e.pointerId){
            if(e.type!=='pointercancel'&&provider.canWrite){const raster=scene.hitRaster(),points=marquee.points,first=points[0],last=points.at(-1);let value;
                if(Math.hypot(last[0]-first[0],last[1]-first[1])<3&&points.length<=2){const text=textBoxes().reverse().find(t=>contains(t.bounds,first)),region=text?{mask:new Uint8Array(w*h),owners:new Set()}:connectedRegion(raster,w,h,...first.slice(0,2));value=region?{region,textIds:new Set(text?[text.id]:[])}:null;}
                else value=selectPolygon(raster,w,h,marquee.kind==='lasso'?points:shape('rectangle',first,last),textBoxes());
                selected=value?selection(value.region,value.textIds):null;
            }marquee=null;draw();return;
        }
        if(selected?.pointer===e.pointerId){
            const value=selected;if(provider.canWrite&&e.type!=='pointercancel'&&(value.dx||value.dy)){
                undo.stopCapturing();let moved=[];
                provider.doc.transact(()=>{
                    for(const id of value.textIds){const box=texts.get(id);if(box){box.set('x',box.get('x')+value.dx);box.set('y',box.get('y')+value.dy);}}
                    if(value.region.owners.size){const parts=splitSelection(ops.toArray(),value.region,w,h,value.dx,value.dy,value.ids);moved=parts.moved;ops.delete(0,ops.length);ops.insert(0,parts.stationary.concat(moved));}
                },local);undo.stopCapturing();
                value.region=shiftRegion(value.region,w,h,value.dx,value.dy);value.ids=new Set(moved.map(s=>s.id));
                value.bounds={left:value.bounds.left+value.dx,top:value.bounds.top+value.dy,right:value.bounds.right+value.dx,bottom:value.bounds.bottom+value.dy};
            }
            value.dx=value.dy=0;delete value.pointer;draw();return;
        }
        if(active?.pointerId!==e.pointerId){return;}
        active.points.push(point(e));if(provider.canWrite&&e.type!=='pointercancel'){
            let stroke=active;if(stroke.tool==='rectangle'||stroke.tool==='ellipse')stroke={...stroke,tool:'pen',points:shape(stroke.tool,stroke.points[0],stroke.points.at(-1),constrain||stroke.shift)};
            provider.doc.transact(()=>ops.push([stroke]),local);undo.stopCapturing();
        }
        active=null;provider.awareness.setLocalStateField('stroke',null);if(e.pointerType!=='mouse'){lastPointer=null;provider.awareness.setLocalStateField('pointer',null);}draw();
    }
    async function changeMeta(next){
        const [w,h]=pageSize(next),[oldW,oldH]=pageSize(getMeta());
        if((w<oldW||h<oldH)&&(ops.length||texts.size)&&!await bridge.invokeMethodAsync('Confirm','Line work outside the new page bounds will be hidden. Enlarging the page brings it back.'))return;
        await bridge.invokeMethodAsync('Meta',next.pageSize,next.orientation,next.pattern);
        provider.doc.transact(()=>Object.entries(next).forEach(([k,v])=>meta.set(k,v)),local);resetView();draw();
    }
    function delay(action,ms){const timer=setTimeout(()=>{timers.delete(timer);if(!disposed)action();},ms);timers.add(timer);}
    async function clear(){
        if(clearing||!provider.canWrite||!await bridge.invokeMethodAsync('Confirm','Clear this sketch? Undo can restore it.'))return;
        if(disposed||!provider.canWrite)return;viewport.querySelector('textarea')?.blur();clearing=true;selected=null;
        const erase=()=>{if(!provider.canWrite)return;undo.stopCapturing();provider.doc.transact(()=>{ops.delete(0,ops.length);for(const key of texts.keys())texts.delete(key);},local);undo.stopCapturing();draw();};
        if(disposed)return;sound.currentTime=0;sound.play().catch(()=>{});
        if(reduced.matches){erase();delay(()=>{clearing=false;},100);return;}
        const blob=await effectReady;if(disposed)return;
        if(!blob){erase();clearing=false;say('Sketch cleared; explosion could not be loaded.');return;}
        const url=URL.createObjectURL(blob);downloads.push(url);explosion.src=url;
        try{await explosion.decode();}catch{}if(disposed)return;
        explosion.hidden=false;transform();delay(erase,595);delay(()=>{explosion.hidden=true;explosion.removeAttribute('src');URL.revokeObjectURL(url);clearing=false;},1190);
    }
    async function command(action,value){
        if(disposed)return;
        if(action==='touchDrawing'){inputMode.touchDrawing=!!value;return;}
        if(action==='zoomIn'){setView(anchoredView(currentView(),center(),zoom*1.25));return;}
        if(action==='zoomOut'){setView(anchoredView(currentView(),center(),zoom/1.25));return;}
        if(action==='fit'){resetView();return;}
        if(action==='export'){
            const output=document.createElement('canvas');paint(output.getContext('2d'),ops.toArray(),getTexts(),getMeta(),{font,paperColor,themeInk:themeInk(),frame:0,exporting:true});
            output.toBlob(blob=>{if(!blob||disposed)return;const url=URL.createObjectURL(blob);downloads.push(url);const a=document.createElement('a');a.href=url;a.download=options.name+'.png';document.body.append(a);a.click();a.remove();},'image/png');return;
        }
        if(!provider.canWrite||clearing)return;
        viewport.querySelector('textarea')?.blur();selected=null;
        if(action==='tool')tool=value;
        else if(action==='ink')ink=value;
        else if(action==='level')level=Number(value);
        else if(action==='constrain')constrain=!!value;
        else if(action==='undo')undo.undo();
        else if(action==='redo')undo.redo();
        else if(action==='clear')await clear();
        else if(action==='settings'){const next=await bridge.invokeMethodAsync('PageSettings',getMeta());if(next&&!disposed&&provider.canWrite)await changeMeta(next);}
        if(lastPointer)updateAwareness(lastPointer);draw();
    }
    function forceWillBegin(e){if(provider.canWrite&&['pen','eraser'].includes(tool))e.preventDefault();}
    function forceChanged(e){
        if(!active||!['pen','eraser'].includes(active.tool)||!provider.canWrite)return;
        const pressure=applyTrackpadPressure(active,e,MouseEvent);if(pressure===null)return;
        e.preventDefault();mousePressure=pressure;
        const last=active.points.at(-1);active.points.push([last[0],last[1],pressure]);
        updateAwareness([last[0],last[1],pressure]);requestDraw();
    }
    function wheel(e){if(e.target.closest('textarea'))return;e.preventDefault();if(!safariGesture)setView(wheelView(currentView(),e,localPoint(e)));}
    function keydown(e){if(e.target.tagName==='TEXTAREA')return;if(e.code==='Space'){space=true;e.preventDefault();}if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='z'){e.preventDefault();command(e.shiftKey?'redo':'undo');}}
    function keyup(e){if(e.code==='Space')space=false;}
    function blur(){space=false;lastPointer=null;provider.awareness.setLocalStateField('pointer',null);}
    const leavePointer=()=>{if(!active){lastPointer=null;provider.awareness.setLocalStateField('pointer',null);}};
    function gestureStart(e){e.preventDefault();active=null;safariGesture={...currentView(),anchor:localPoint(e)};}
    function gestureChange(e){if(!safariGesture)return;e.preventDefault();setView(anchoredView(safariGesture,safariGesture.anchor,safariGesture.zoom*e.scale,safariGesture.angle+e.rotation));}
    function gestureEnd(e){e.preventDefault();safariGesture=null;}
    const dialog=root.closest('.design-docs-sketch-dialog');
    const editable=target=>target?.closest?.('textarea,input,[contenteditable=true]');
    const preventSelection=event=>{if(!editable(event.target))event.preventDefault();};
    // iPadOS can select text without selectstart. Cancel native touch defaults too.
    const clearNativeSelection=()=>{const selection=document.getSelection();if(!editable(document.activeElement)&&selection?.rangeCount)selection.removeAllRanges();};
    const preventNativeTouch=event=>{if(!editable(event.target)){event.preventDefault();clearNativeSelection();}};
    const nativeTouchOptions={capture:true,passive:false};
    const preventPalm=event=>{if(event.pointerType==='touch'&&inputMode.penId!==null&&!event.target.closest('textarea')){event.preventDefault();event.stopPropagation();}};
    dialog?.addEventListener('selectstart',preventSelection,true);
    dialog?.addEventListener('contextmenu',preventSelection,true);
    document.addEventListener('selectionchange',clearNativeSelection);
    clearNativeSelection();
    for(const type of ['touchstart','touchmove','touchend'])viewport.addEventListener(type,preventNativeTouch,nativeTouchOptions);
    for(const type of ['mousedown','dblclick'])viewport.addEventListener(type,preventSelection,true);
    dialog?.addEventListener('pointerdown',preventPalm,true);
    viewport.addEventListener('pointerdown',down);viewport.addEventListener('pointermove',move);viewport.addEventListener('pointerup',up);viewport.addEventListener('pointercancel',up);
    canvas.addEventListener('webkitmouseforcewillbegin',forceWillBegin);canvas.addEventListener('webkitmouseforcechanged',forceChanged);
    viewport.addEventListener('wheel',wheel,{passive:false});viewport.addEventListener('keydown',keydown);viewport.addEventListener('keyup',keyup);viewport.addEventListener('blur',blur);viewport.addEventListener('pointerleave',leavePointer);
    viewport.addEventListener('gesturestart',gestureStart,{passive:false});viewport.addEventListener('gesturechange',gestureChange,{passive:false});viewport.addEventListener('gestureend',gestureEnd,{passive:false});
    const stopDesktopRotation=bindDesktopRotation(viewport,currentView,view=>{if(!active&&!marquee&&!dragPan&&inputMode.penId===null)setView(view);});
    const resize=new ResizeObserver(()=>{if(fitPending&&viewport.clientWidth&&viewport.clientHeight)resetView();});resize.observe(viewport);
    draw();return {command,connected:value=>provider.connected(value),receive:(kind,data)=>provider.receive(kind,data),presence:users=>provider.presence(users),
        dispose(){disposed=true;peerCursors.dispose();stopDesktopRotation();scene.dispose();themeObserver.disconnect();dialog?.removeEventListener('selectstart',preventSelection,true);dialog?.removeEventListener('contextmenu',preventSelection,true);document.removeEventListener('selectionchange',clearNativeSelection);for(const type of ['touchstart','touchmove','touchend'])viewport.removeEventListener(type,preventNativeTouch,true);for(const type of ['mousedown','dblclick'])viewport.removeEventListener(type,preventSelection,true);dialog?.removeEventListener('pointerdown',preventPalm,true);resize.disconnect();for(const timer of timers)clearTimeout(timer);sound.pause();sound.removeAttribute('src');if(frameRequest!==null)cancelAnimationFrame(frameRequest);clearInterval(boil);observer.disconnect();undo.destroy();provider.dispose();downloads.forEach(url=>URL.revokeObjectURL(url));viewport.removeEventListener('pointerdown',down);viewport.removeEventListener('pointermove',move);viewport.removeEventListener('pointerup',up);viewport.removeEventListener('pointercancel',up);viewport.removeEventListener('wheel',wheel);viewport.removeEventListener('keydown',keydown);viewport.removeEventListener('keyup',keyup);viewport.removeEventListener('blur',blur);viewport.removeEventListener('pointerleave',leavePointer);viewport.removeEventListener('gesturestart',gestureStart);viewport.removeEventListener('gesturechange',gestureChange);viewport.removeEventListener('gestureend',gestureEnd);canvas.removeEventListener('webkitmouseforcewillbegin',forceWillBegin);canvas.removeEventListener('webkitmouseforcechanged',forceChanged);root.replaceChildren();}};
}
