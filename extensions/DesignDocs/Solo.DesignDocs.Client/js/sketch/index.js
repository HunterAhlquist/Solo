import {sketchStrokes,moveStroke} from './objects.js';
import { presenceTheme,SketchPresence } from '../presence.js';
import { bindDesktopRotation } from './rotation.js';
import * as Y from 'yjs';
import { Provider } from '../sync/provider.js';
import { pageSize,paint,connectedRegion,splitSelection,shape,pressureWidth } from './render.js';
import { loadFont,textSize,textLayout,uniformFace,runFont } from './font.js';
import { rangeAttributes } from './text-format.js';
import { caretAt,selectionRects,offsetAt,shiftAnchor,wordAt } from './text-caret.js';
import { panelPosition } from './text-panel.js';
import { pagePoint,anchoredView,wheelView,touchView } from './viewport.js';
import { contains,selectPolygon,selectionBounds,shiftRegion } from './selection.js';
import { SketchScene } from './scene.js';
import { SketchInput } from './input.js';
import { applyTrackpadPressure } from './pressure.js';
import { strokePresence,StrokeFeed } from './stroke-presence.js';
const local=Symbol('local sketch');
export async function mountSketch(root,bridge,options){
    const font=await loadFont(root),shell=root.closest('.design-docs-sketch-shell')??root;
    let frameRequest=null,active=null,selected=null,marquee=null,editing=null,editingId=null,frame=0,disposed=false,visible=true;
    let caretOn=true,caretTimer=null,textDragCleanup=null;
    let pan=[12,12],zoom=1,angle=0,gesture=null,dragPan=null,space=false,safariGesture=null,fitPending=true;
    let mousePressure=0,lastPointer=null,lastPointerType='mouse',delivered={id:null,count:0};
    const feed=new StrokeFeed();
    let tool='pen',ink='theme',level=0,constrain=false,clearing=false,closing=false;
    const commands=new Set();
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
    const provider=new Provider({send:(kind,data)=>bridge.invokeMethodAsync('Send',kind,data)}, {canWrite:options.canWrite,user:options.user,documentId:options.documentId,projectId:options.projectId,vaultId:options.vaultId,atomicSync:true,hasDraft:()=>!!active||(selected?.pointer!==undefined&&(selected.dx!==0||selected.dy!==0))||clearing||commands.size>0,
        status:message=>{say(message);if(provider.synced&&provider.canWrite&&!meta.size)provider.doc.transact(()=>Object.entries(options.meta).forEach(([k,v])=>meta.set(k,v)),local);},
        // Presence that reached the relay; the next message starts after it. A message holds
        // at most one bounded chunk, so while samples remain unsent (a newcomer being filled
        // in) the next chunk goes out now, rather than waiting for the pen to move.
        awarenessSent:state=>{const stroke=state?.stroke;if(!stroke||stroke.id!==active?.id)return;
            delivered={id:stroke.id,count:Math.max(delivered.id===stroke.id?delivered.count:0,stroke.base+stroke.points.length)};
            if(delivered.count<active.points.length&&provider.canWrite)provider.awareness.setLocalStateField('stroke',heldPresence());},
        // Someone arriving mid-stroke has none of it yet.
        peerJoined:()=>{delivered={id:null,count:0};if(active&&provider.canWrite)provider.awareness.setLocalStateField('stroke',heldPresence());},
        writable:value=>{bridge.invokeMethodAsync('Writable',value&&!closing);if(!value){finishStroke();if(selected?.pointer!==undefined)up({pointerId:selected.pointer,type:'pointerup'},true);selected=null;marquee=null;viewport.querySelector('textarea')?.blur();requestDraw();}},
    });
    const ops=provider.doc.getArray('ops'),texts=provider.doc.getMap('texts'),meta=provider.doc.getMap('meta'),cuts=provider.doc.getMap('cuts');
    const undo=new Y.UndoManager([ops,texts,meta,cuts],{trackedOrigins:new Set([local])});
    const getMeta=()=>({...options.meta,...meta.toJSON()});
    // `runs` is the Y.Text delta: the box's formatting, flattened for the
    // renderer. toJSON gives only the plain string, which is what the box's own
    // ink and size still apply to.
    const getTexts=()=>[...texts].map(([id,value])=>({id,...value.toJSON(),runs:value.get('text')?.toDelta?.()??null}));
    let inkColor,fontFamily,paperColor,primaryColor,themeKey;
    const themeInk=()=>inkColor;
    function colors(){const style=getComputedStyle(root),key=[style.color,style.fontFamily,style.getPropertyValue('--mud-palette-surface'),style.getPropertyValue('--mud-palette-primary')].join('|');
        if(key===themeKey)return;themeKey=key;inkColor=style.color;fontFamily=style.fontFamily;paperColor=style.getPropertyValue('--mud-palette-surface')||'white';
        primaryColor=style.getPropertyValue('--mud-palette-primary')||inkColor;
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
        if(input&&editing){const box=editingBox(),layout=textLayout(context,box,fontFamily),face=uniformFace(layout);
            input.style.transform=value+` translate(${box.x}px,${box.y}px)`;
            // One font for all of its text is all a textarea has. Where every run
            // agrees it is given theirs, so the editor's own line stepping and
            // scrolling match what was drawn; where they differ the box default
            // stands in and none of its geometry is trusted — the caret, the
            // highlight and the hit test are all read from `layout` instead.
            input.style.font=face?runFont(face,fontFamily):`${textSize(box)}px ${fontFamily}`;input.style.lineHeight='1.5';
            input.style.width=Math.max(120,layout.width+24)+'px';input.style.height=Math.max(textSize(box)*1.5,layout.height)+'px';
            placePanel(input);
        }
    }
    const editingBox=()=>({...editing.toJSON(),text:editing.get('text').toString(),runs:editing.get('text').toDelta()});
    // The formatting panel floats over the box being edited rather than parking
    // beside the ink palette: the page is far larger than the view, and a panel
    // in the corner makes the author look away from what they are formatting.
    // It is Blazor's markup, so it is positioned through custom properties
    // instead of by reaching into it.
    function placePanel(input){
        const panel=shell.querySelector('.design-docs-sketch-text-palette');
        const rect=input.getBoundingClientRect(),view=viewport.getBoundingClientRect();
        // The dotted outline stands 3px off the box; the gap clears that too.
        const position=panelPosition(
            {left:rect.left-view.left-4,top:rect.top-view.top-4,bottom:rect.bottom-view.top+4,width:rect.width+8},
            {width:viewport.clientWidth,height:viewport.clientHeight},
            {width:panel?.offsetWidth||220,height:panel?.offsetHeight||48});
        shell.style.setProperty('--design-docs-text-panel-x',position.x+'px');
        shell.style.setProperty('--design-docs-text-panel-y',position.y+'px');
    }
    function textBoxes(){return getTexts().map(t=>({...t,bounds:font.bounds(context,t)}));}
    function selection(region,textIds=new Set()){
        const [w]=pageSize(getMeta()),bounds=selectionBounds(region,w,textBoxes().filter(t=>textIds.has(t.id)).map(t=>t.bounds));
        return bounds?{region,textIds,bounds,ids:new Set([...region.owners].map(n=>sketchStrokes(provider.doc)[n-1]?.id)),dx:0,dy:0}:null;
    }
    function draw(){
        if(disposed)return;const preview=[];
        if(active)preview.push(active.tool==='rectangle'||active.tool==='ellipse'?{...active,tool:'pen',points:shape(active.tool,active.points[0],active.points.at(-1),constrain||active.shift)}:active);
        for(const stroke of feed.values())preview.push(stroke);
        const floating=scene.render(context,{texts:getTexts(),extra:preview,selection:selected,selectionContext,font,frame:reduced.matches?0:frame});
        selectionCanvas.hidden=!floating;selectionPadding=floating?.padding??0;
        if(selected){const b=selected.bounds,c=floating?selectionContext:context,dx=floating?selectionPadding:selected.dx,dy=floating?selectionPadding:selected.dy,x=b.left+dx,y=b.top+dy;c.strokeStyle=themeInk();c.lineWidth=1/zoom;c.setLineDash([5/zoom,4/zoom]);c.strokeRect(x-3,y-3,b.right-b.left+6,b.bottom-b.top+6);c.setLineDash([]);
            for(const [cx,cy]of [[x,y],[b.right+dx,y],[x,b.bottom+dy],[b.right+dx,b.bottom+dy]]){c.fillStyle=themeInk();c.fillRect(cx-2/zoom,cy-2/zoom,4/zoom,4/zoom);}}
        if(editing)drawText();
        if(marquee){const points=marquee.kind==='lasso'?marquee.points:shape('rectangle',marquee.points[0],marquee.points.at(-1));context.strokeStyle=themeInk();context.lineWidth=1/zoom;context.setLineDash([5/zoom,4/zoom]);context.beginPath();points.forEach((p,i)=>i?context.lineTo(...p.slice(0,2)):context.moveTo(...p.slice(0,2)));context.closePath();context.stroke();context.setLineDash([]);}
        transform();
    }
    // The editor's own caret and highlight are hidden in CSS and drawn here
    // instead, from the layout the glyphs came from. A textarea has one font, so
    // its versions kept the geometry of unformatted text: a 32px word came out
    // under a 24px band, which is the drift this removes.
    function drawText(){
        const input=viewport.querySelector('textarea');
        if(!input||!editing)return;
        const box=editingBox(),layout=textLayout(context,box,fontFamily),start=input.selectionStart,end=input.selectionEnd;
        if(end>start){
            // Over the glyphs rather than behind them: the scene is painted in one
            // pass, and 25% is what the textarea's own highlight used to lay there.
            context.save();context.globalAlpha=.25;context.fillStyle=primaryColor;
            for(const rect of selectionRects(context,layout,fontFamily,start,end))context.fillRect(box.x+rect.x,box.y+rect.y,rect.width,rect.height);
            context.restore();
        }else if(caretOn&&document.activeElement===input){
            const caret=caretAt(context,layout,fontFamily,start);
            context.fillStyle=themeInk();context.fillRect(box.x+caret.x-.75/zoom,box.y+caret.y,1.5/zoom,caret.height);
        }
    }
    // Restarted on every caret move so it is solid where the author just typed,
    // which is also what makes it visible at all between keystrokes.
    function blinkCaret(){
        stopCaret();
        if(reduced.matches)return;
        caretTimer=setInterval(()=>{caretOn=!caretOn;requestDraw();},530);
    }
    function stopCaret(){if(caretTimer!==null)clearInterval(caretTimer);caretTimer=null;caretOn=true;}
    const requestDraw=()=>{if(frameRequest!==null||disposed)return;frameRequest=requestAnimationFrame(()=>{frameRequest=null;draw();});};
    const sceneChanged=()=>{scene.setScene(sketchStrokes(provider.doc),getMeta());requestDraw();};
    ops.observe(sceneChanged);cuts.observe(sceneChanged);texts.observeDeep(requestDraw);meta.observe(()=>{sceneChanged();if(fitPending)resetView();});sceneChanged();provider.awareness.on('change',({added,updated,removed})=>{
        // Fold each message in as it arrives; several can land within one frame.
        for(const id of [...added,...updated])if(id!==provider.doc.clientID)feed.apply(id,provider.awareness.getStates().get(id)?.stroke);
        for(const id of removed)feed.delete(id);requestDraw();
    });
    const observer=new IntersectionObserver(entries=>{visible=entries[0].isIntersecting;});observer.observe(viewport);
    const boil=setInterval(()=>{if(visible&&!document.hidden&&!disposed&&!reduced.matches&&ops.length){frame=(frame+1)%3;requestDraw();}},250);
    function localPoint(e){if(!Number.isFinite(e.clientX)||!Number.isFinite(e.clientY))return center();const r=viewport.getBoundingClientRect();return [e.clientX-r.left,e.clientY-r.top];}
    function point(e){return [...pagePoint(currentView(),localPoint(e)),e.pointerType==='mouse'?mousePressure:e.pressure];}
    function updateAwareness(p,type=lastPointerType){lastPointer=p;lastPointerType=type;if(!provider.canWrite)return;provider.awareness.setLocalStateField('pointer',{x:p[0],y:p[1],tool,width:pressureWidth(level,p[2]??0,type)});provider.awareness.setLocalStateField('stroke',heldPresence());}
    const heldPresence=()=>strokePresence(active,delivered.id===active?.id?delivered.count:0);
    function editText(box,id){
        viewport.querySelector('textarea')?.blur();editing=box;editingId=id;const text=box.get('text'),input=document.createElement('textarea');input.className='design-docs-sketch-text';input.setAttribute('aria-label','Sketch text');input.value=text.toString();input.spellcheck=false;
        viewport.append(input);transform();undo.stopCapturing();
        input.addEventListener('input',()=>{if(!provider.canWrite)return;const before=text.toString(),after=input.value;let start=0,end=0;while(start<before.length&&start<after.length&&before[start]===after[start])start++;while(end<before.length-start&&end<after.length-start&&before[before.length-1-end]===after[after.length-1-end])end++;
            provider.doc.transact(()=>{text.delete(start,before.length-start-end);text.insert(start,after.slice(start,after.length-end));},local);draw();});
        const reflect=()=>{const before=input.value,after=text.toString();if(before!==after){const cursor=input.selectionStart;input.value=after;input.setSelectionRange(Math.min(after.length,cursor),Math.min(after.length,cursor));}requestDraw();reportFormat();};
        // Everything that can move the selection. 'selectionchange' on a
        // textarea is still not universal, so the pointer and key events that
        // precede it are listened for too; reportFormat only sends on a change.
        for(const name of ['select','selectionchange','keyup','mouseup','input','focus'])input.addEventListener(name,reportFormat);

        // A click is placed from the layout too. The textarea answers from its
        // own single font, so with mixed runs the character it picks is not the
        // one under the pointer — and the caret would land beside the glyph that
        // was clicked.
        const offsetFrom=event=>{
            const p=pagePoint(currentView(),localPoint(event)),current=editingBox();
            return offsetAt(context,textLayout(context,current,fontFamily),fontFamily,p[0]-current.x,p[1]-current.y);
        };
        const setRange=(from,to)=>input.setSelectionRange(Math.min(from,to),Math.max(from,to),to<from?'backward':'forward');
        let anchor=null;
        const extend=event=>{if(anchor!==null){setRange(anchor,offsetFrom(event));reportFormat();}};
        const endDrag=()=>{anchor=null;document.removeEventListener('mousemove',extend);document.removeEventListener('mouseup',endDrag);};
        textDragCleanup=endDrag;
        input.addEventListener('mousedown',event=>{
            if(event.button!==0)return;
            // This cancels the textarea's own caret placement and its focus
            // together, so focus is taken by hand; without that the blur handler
            // below would tear the editor down in the middle of the click.
            event.preventDefault();input.focus();
            const index=offsetFrom(event);
            anchor=event.shiftKey?shiftAnchor(input):index;
            setRange(anchor,index);reportFormat();
            document.addEventListener('mousemove',extend);document.addEventListener('mouseup',endDrag);
        });
        input.addEventListener('dblclick',event=>{event.preventDefault();const [from,to]=wordAt(input.value,offsetFrom(event));setRange(from,to);reportFormat();});
        text.observe(reflect);input.addEventListener('blur',()=>{endDrag();textDragCleanup=null;text.unobserve(reflect);input.remove();editing=null;editingId=null;undo.stopCapturing();reportFormat();draw();},{once:true});input.focus();draw();reportFormat();
    }
    // Deletes the box, not its text: the panel's own control, so it is in reach
    // while editing rather than needing the box selected first. Undo restores it.
    function deleteText(){
        if(!editing||!provider.canWrite)return;
        const id=editingId;
        undo.stopCapturing();viewport.querySelector('textarea')?.blur();
        if(id!=null&&texts.has(id))provider.doc.transact(()=>texts.delete(id),local);
        undo.stopCapturing();selected=null;draw();
    }

    // -- text formatting ----------------------------------------------------
    // The textarea is transparent and the canvas draws the text, so the panel
    // acts on the textarea's selection and writes Y.Text attributes over the
    // same range. Reported back to the dialog rather than inferred there: only
    // this side knows the delta.
    let lastFormat='';
    function formatState(){
        const input=viewport.querySelector('textarea');
        if(!editing||!input)return null;
        const box={...editing.toJSON()},delta=editing.get('text').toDelta();
        return {...rangeAttributes(delta,input.selectionStart,input.selectionEnd,{size:textSize(box),ink:box.ink}),
            selected:input.selectionEnd>input.selectionStart};
    }
    function reportFormat(){
        // The caret and the highlight are canvas now, so anything that moves the
        // selection has to repaint as well as report.
        requestDraw();if(editing)blinkCaret();else stopCaret();
        const state=formatState(),message=JSON.stringify(state);
        // Only on a change: these events fire on every keystroke and the panel
        // redraw is a round trip into Blazor.
        if(message===lastFormat)return;
        lastFormat=message;bridge.invokeMethodAsync('TextFormat',message);
    }
    // Applies `attributes` to the selection, or to the whole box when the caret
    // is not a range — pressing B with nothing selected is taken as "this box",
    // which is what makes the panel useful before any text exists.
    function formatSelection(attributes){
        const input=viewport.querySelector('textarea');
        if(!editing||!input||!provider.canWrite)return;
        const text=editing.get('text'),range=input.selectionEnd>input.selectionStart;
        // A caret is taken as "this box": pressing B before selecting anything
        // formats what is there, rather than doing nothing. An empty box has
        // nothing to format and falls out here.
        const from=range?input.selectionStart:0,to=range?input.selectionEnd:text.length;
        if(to<=from)return;
        undo.stopCapturing();
        provider.doc.transact(()=>text.format(from,to-from,attributes),local);
        // Formatting must not move the caret: the canvas is what changed, and the
        // author is mid-edit. Only a range needs restoring — the whole-box case
        // never touched the selection.
        input.focus();if(range)input.setSelectionRange(from,to);
        lastFormat='';reportFormat();draw();
    }
    // A toggle reads the range first: a selection that is partly bold goes fully
    // bold rather than flipping each run, which is what every editor does.
    function toggleFormat(key){
        const state=formatState();
        if(state)formatSelection({[key]:state[key]!==true?true:null});
    }
    function down(e){
        if(e.target.closest('textarea')||e.button>1||clearing||closing)return;
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
            const existing=textBoxes().reverse().find(t=>contains(t.bounds,p));if(existing){editText(texts.get(existing.id),existing.id);return;}
            const id=crypto.randomUUID(),box=new Y.Map(),text=new Y.Text();undo.stopCapturing();provider.doc.transact(()=>{box.set('x',Math.round(p[0]));box.set('y',Math.round(p[1]));box.set('ink',ink);box.set('scale',2);box.set('text',text);texts.set(id,box);},local);editText(box,id);return;
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
    function up(e,retainDraft=false){
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
            const value=selected;if((provider.canWrite||retainDraft)&&e.type!=='pointercancel'&&(value.dx||value.dy)){
                undo.stopCapturing();let moved=[];
                provider.doc.transact(()=>{
                    for(const id of value.textIds){const box=texts.get(id);if(box){box.set('x',box.get('x')+value.dx);box.set('y',box.get('y')+value.dy);}}
                    if(value.region.owners.size){
                        for(const stroke of sketchStrokes(provider.doc).filter(s=>value.ids.has(s.id)&&s.tool!=='eraser')){
                            const parts=splitSelection([stroke],value.region,w,h,value.dx,value.dy,value.ids);
                            moved.push(...moveStroke(provider.doc,stroke,parts));
                        }
                    }
                },local);undo.stopCapturing();
                value.region=shiftRegion(value.region,w,h,value.dx,value.dy);value.ids=new Set(moved.map(s=>s.id));
                value.bounds={left:value.bounds.left+value.dx,top:value.bounds.top+value.dy,right:value.bounds.right+value.dx,bottom:value.bounds.bottom+value.dy};
            }
            value.dx=value.dy=0;delete value.pointer;draw();return;
        }
        if(active?.pointerId!==e.pointerId){return;}
        active.points.push(point(e));finishStroke(e.type!=='pointercancel');
    }
    function finishStroke(commit=true){
        if(!active)return;
        if(options.canWrite&&commit){
            let stroke=active;if(stroke.tool==='rectangle'||stroke.tool==='ellipse')stroke={...stroke,tool:'pen',points:shape(stroke.tool,stroke.points[0],stroke.points.at(-1),constrain||stroke.shift)};
            provider.doc.transact(()=>ops.push([stroke]),local);undo.stopCapturing();
        }
        const pointerType=active.pointerType;inputMode.finish(active);active=null;
        provider.awareness.setLocalStateField('stroke',null);if(pointerType!=='mouse'){lastPointer=null;provider.awareness.setLocalStateField('pointer',null);}draw();
    }
    async function waitForSave(timeout=10000){
        const deadline=Date.now()+timeout;
        finishStroke();
        if(selected?.pointer!==undefined)up({pointerId:selected.pointer,type:'pointerup'},true);
        viewport.querySelector('textarea')?.blur();closing=true;
        await bridge.invokeMethodAsync('Writable',false);
        try{
            while((commands.size||clearing)&&Date.now()<deadline&&!disposed)await new Promise(resolve=>setTimeout(resolve,25));
            if(commands.size||clearing||disposed)return false;
            return await provider.waitForSave(Math.max(0,deadline-Date.now()));
        }finally{closing=false;if(!disposed)await bridge.invokeMethodAsync('Writable',provider.canWrite);}
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
        if(disposed||closing)return;
        if(action==='touchDrawing'){inputMode.touchDrawing=!!value;return;}
        if(action==='zoomIn'){setView(anchoredView(currentView(),center(),zoom*1.25));return;}
        if(action==='zoomOut'){setView(anchoredView(currentView(),center(),zoom/1.25));return;}
        if(action==='fit'){resetView();return;}
        if(action==='export'){
            const output=document.createElement('canvas');paint(output.getContext('2d'),sketchStrokes(provider.doc),getTexts(),getMeta(),{font,paperColor,themeInk:themeInk(),frame:0,exporting:true});
            output.toBlob(blob=>{if(!blob||disposed)return;const url=URL.createObjectURL(blob);downloads.push(url);const a=document.createElement('a');a.href=url;a.download=options.name+'.png';document.body.append(a);a.click();a.remove();},'image/png');return;
        }
        if(!provider.canWrite||clearing)return;

        // Before the blur below, and deliberately: every other write command
        // drops the text editor, and these act on the selection inside it.
        if(action==='bold'||action==='italic'||action==='underline'){toggleFormat(action);return;}
        if(action==='textSize'){formatSelection({size:Number(value)||null});return;}
        if(action==='deleteText'){deleteText();return;}
        // Ink is two commands in one. With the text editor open it paints the
        // selection; otherwise it is the pen's colour, as it always was.
        // Named explicitly rather than cleared. Clearing makes the run inherit the
        // *box's* ink, which is whatever the pen was when the box was created —
        // so on a box created in red, "theme" left the text red and the palette
        // lit red, with no way to reach theme ink at all.
        if(action==='ink'&&editing){formatSelection({ink:value});return;}

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
    const closeKey=event=>{if(event.key==='Escape'){event.preventDefault();event.stopPropagation();bridge.invokeMethodAsync('Close');}};
    dialog?.addEventListener('keydown',closeKey,true);
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
    // Blazor renders the formatting panel a frame after the editor opens, and
    // placing it needs its measured size. Its arrival is the signal.
    const shellObserver=new MutationObserver(()=>{if(editing)transform();});shellObserver.observe(shell,{childList:true});
    const removeSaveGuard=globalThis.solo?.offline?.guard(waitForSave);
    // Closing saves, then tells the note's viewers once that their preview is stale.
    async function close(timeout){const saved=await waitForSave(timeout);if(saved&&!disposed)await provider.closed();return saved;}
    draw();return {waitForSave,close,command:(...args)=>{const task=command(...args);commands.add(task);task.finally(()=>commands.delete(task)).catch(()=>{});return task;},connected:value=>provider.connected(value),deleted:()=>provider.markDeleted(),unsaved:()=>provider.hasPending||!!provider.options.hasDraft?.(),discard:()=>provider.discard(),receive:(kind,data)=>provider.receive(kind,data),presence:users=>provider.presence(users),
        dispose(){disposed=true;stopCaret();textDragCleanup?.();shellObserver.disconnect();removeSaveGuard?.();dialog?.removeEventListener('keydown',closeKey,true);peerCursors.dispose();stopDesktopRotation();scene.dispose();themeObserver.disconnect();dialog?.removeEventListener('selectstart',preventSelection,true);dialog?.removeEventListener('contextmenu',preventSelection,true);document.removeEventListener('selectionchange',clearNativeSelection);for(const type of ['touchstart','touchmove','touchend'])viewport.removeEventListener(type,preventNativeTouch,true);for(const type of ['mousedown','dblclick'])viewport.removeEventListener(type,preventSelection,true);dialog?.removeEventListener('pointerdown',preventPalm,true);resize.disconnect();for(const timer of timers)clearTimeout(timer);sound.pause();sound.removeAttribute('src');if(frameRequest!==null)cancelAnimationFrame(frameRequest);clearInterval(boil);observer.disconnect();undo.destroy();provider.dispose();downloads.forEach(url=>URL.revokeObjectURL(url));viewport.removeEventListener('pointerdown',down);viewport.removeEventListener('pointermove',move);viewport.removeEventListener('pointerup',up);viewport.removeEventListener('pointercancel',up);viewport.removeEventListener('wheel',wheel);viewport.removeEventListener('keydown',keydown);viewport.removeEventListener('keyup',keyup);viewport.removeEventListener('blur',blur);viewport.removeEventListener('pointerleave',leavePointer);viewport.removeEventListener('gesturestart',gestureStart);viewport.removeEventListener('gesturechange',gestureChange);viewport.removeEventListener('gestureend',gestureEnd);canvas.removeEventListener('webkitmouseforcewillbegin',forceWillBegin);canvas.removeEventListener('webkitmouseforcechanged',forceChanged);root.replaceChildren();}};
}
