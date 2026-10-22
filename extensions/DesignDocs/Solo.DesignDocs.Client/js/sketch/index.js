import * as Y from 'yjs';
import { Provider } from '../sync/provider.js';
import { pageSize,paint,rasterize,connectedRegion,splitMove,shape } from './render.js';
import { loadFont } from './font.js';
const local=Symbol('local sketch');
export async function mountSketch(root,bridge,options){
    const font=await loadFont(options.fontUrl);let canvas,context,active=null,selected=null,editing=null,raster,frame=0,disposed=false,visible=true,penDown=false;
    let pan=[12,12],zoom=1,angle=0,gesture=null;const touches=new Map(),downloads=[];
    const toolbar=document.createElement('div');toolbar.className='design-docs-sketch-tools';
    const viewport=document.createElement('div');viewport.className='design-docs-sketch-viewport';viewport.tabIndex=0;
    canvas=document.createElement('canvas');canvas.tabIndex=0;canvas.setAttribute('aria-label','Collaborative sketch');canvas.style.transformOrigin='0 0';context=canvas.getContext('2d');
    const status=document.createElement('output');status.setAttribute('aria-live','polite');
    root.append(toolbar,status,viewport);viewport.append(canvas);
    const say=message=>{status.textContent=message;};
    const controls=[];
    function select(label,items,value){const l=document.createElement('label');l.textContent=label+' ';const input=document.createElement('select');input.setAttribute('aria-label',label);
        for(const [v,text]of items){const o=document.createElement('option');o.value=v;o.textContent=text;input.append(o);}input.value=value;l.append(input);toolbar.append(l);controls.push(input);return input;}
    function button(label,action,editingOnly=false){const b=document.createElement('button');b.type='button';b.textContent=label;b.onclick=action;toolbar.append(b);if(editingOnly)controls.push(b);return b;}
    const tool=select('Tool',['pen','eraser','rectangle','ellipse','select','text'].map(t=>[t,t]),'pen');
    const ink=select('Ink',[['theme','Theme ink'],['red','Red'],['blue','Blue'],['green','Green']],'theme');
    const level=select('Thickness',[1,2,3,5,8].map((w,i)=>[String(i),`${i+1} (${w} px)`]),'0');
    const constrainLabel=document.createElement('label'),constrain=document.createElement('input');constrain.type='checkbox';constrainLabel.append(constrain,' Constrain');toolbar.append(constrainLabel);controls.push(constrain);
    const paper=select('Paper',Object.keys({A3:0,A4:0,A5:0,Letter:0}).map(v=>[v,v]),options.meta.pageSize);
    const orientation=select('Orientation',[['portrait','Portrait'],['landscape','Landscape']],options.meta.orientation);
    const pattern=select('Pattern',['empty','grid','notebook','dots'].map(v=>[v,v]),options.meta.pattern);
    const provider=new Provider({send:(kind,data)=>bridge.invokeMethodAsync('Send',kind,data)}, {canWrite:options.canWrite,user:options.user,
        status:message=>{say(message);if(provider?.synced&&provider.canWrite&&!meta.size)provider.doc.transact(()=>Object.entries(options.meta).forEach(([k,v])=>meta.set(k,v)),local);},
        writable:value=>{controls.forEach(c=>c.disabled=!value);},
    });
    const ops=provider.doc.getArray('ops'),texts=provider.doc.getMap('texts'),meta=provider.doc.getMap('meta');
    const undo=new Y.UndoManager([ops,texts,meta],{trackedOrigins:new Set([local])});
    const getMeta=()=>({...options.meta,...meta.toJSON()});
    const getTexts=()=>[...texts].map(([id,value])=>({id,...value.toJSON()}));
    const reduced=matchMedia('(prefers-reduced-motion: reduce)');
    function themeInk(){const value=getComputedStyle(viewport).backgroundColor.match(/[\d.]+/g);return value&&value.slice(0,3).map(Number).reduce((a,b)=>a+b,0)<384?'#fff':'#111';}
    function transform(){canvas.style.transform=`translate(${pan[0]}px,${pan[1]}px) rotate(${angle}deg) scale(${zoom})`;}
    function draw(){if(disposed)return;const current=getMeta(),base=ops.toArray(),[w,h]=pageSize(current);raster=rasterize(base,w,h,reduced.matches?0:frame);
        const preview=[...base];
        if(active)preview.push(active.tool==='rectangle'||active.tool==='ellipse'?{...active,tool:'pen',points:shape(active.tool,active.points[0],active.points.at(-1),constrain.checked||active.shift)}:active);
        const others=[...provider.awareness.getStates()].filter(([id])=>id!==provider.doc.clientID).map(([,state])=>state);
        for(const state of others)if(state.stroke)preview.push(state.stroke);
        paint(context,preview,getTexts(),current,{font,frame:reduced.matches?0:frame,themeInk:themeInk()});
        for(const state of others)if(state.pointer){const {x,y}=state.pointer;context.strokeStyle=state.user?.color??'#b33';context.lineWidth=1;context.strokeRect(x-3,y-3,6,6);context.fillStyle=state.user?.color??'#b33';context.font='11px sans-serif';context.fillText(state.user?.name??'Writer',x+6,y-4);}

        if(selected){context.strokeStyle='#c68a20';context.lineWidth=1;context.setLineDash([3,3]);context.strokeRect(selected.x+selected.dx-4,selected.y+selected.dy-4,8,8);context.setLineDash([]);}
        paper.value=current.pageSize;orientation.value=current.orientation;pattern.value=current.pattern;transform();
    }
    ops.observe(draw);texts.observeDeep(draw);meta.observe(draw);provider.awareness.on('change',draw);
    const observer=new IntersectionObserver(entries=>{visible=entries[0].isIntersecting;});observer.observe(viewport);
    const boil=setInterval(()=>{if(visible&&!document.hidden&&!disposed){frame=reduced.matches?0:(frame+1)%3;draw();}},250);
    function point(e){const r=viewport.getBoundingClientRect(),x=(e.clientX-r.left-pan[0])/zoom,y=(e.clientY-r.top-pan[1])/zoom,a=-angle*Math.PI/180;return [Math.cos(a)*x-Math.sin(a)*y,Math.sin(a)*x+Math.cos(a)*y,e.pressure];}
    function updateAwareness(p){if(!provider.canWrite)return;provider.awareness.setLocalStateField('pointer',{x:p[0],y:p[1]});provider.awareness.setLocalStateField('stroke',active&&['pen','eraser'].includes(active.tool)?active:null);}
    function editText(box){editing=box;const text=box.get('text');const input=document.createElement('textarea');input.className='design-docs-sketch-text';input.setAttribute('aria-label','Sketch text');input.value=text.toString();
        input.style.left=`${pan[0]+box.get('x')*zoom}px`;input.style.top=`${pan[1]+box.get('y')*zoom}px`;viewport.append(input);
        input.addEventListener('input',()=>{const before=text.toString(),after=input.value;let start=0,end=0;while(start<before.length&&start<after.length&&before[start]===after[start])start++;while(end<before.length-start&&end<after.length-start&&before[before.length-1-end]===after[after.length-1-end])end++;
            provider.doc.transact(()=>{text.delete(start,before.length-start-end);text.insert(start,after.slice(start,after.length-end));},local);});
        const reflect=()=>{const before=input.value,after=text.toString();if(before===after)return;let prefix=0;while(prefix<before.length&&prefix<after.length&&before[prefix]===after[prefix])prefix++;
            const cursor=input.selectionStart+(input.selectionStart>prefix?after.length-before.length:0);input.value=after;input.setSelectionRange(Math.max(0,cursor),Math.max(0,cursor));};
        text.observe(reflect);input.addEventListener('blur',()=>{text.unobserve(reflect);input.remove();editing=null;draw();},{once:true});input.focus();
    }
    function down(e){if(e.pointerType==='touch'&&penDown)return;e.preventDefault();canvas.setPointerCapture(e.pointerId);
        if(e.pointerType==='touch'){touches.set(e.pointerId,[e.clientX,e.clientY]);if(touches.size===2){active=null;const [a,b]=[...touches.values()];gesture={pan:[...pan],zoom,angle,mid:[(a[0]+b[0])/2,(a[1]+b[1])/2],distance:Math.hypot(a[0]-b[0],a[1]-b[1]),rotation:Math.atan2(b[1]-a[1],b[0]-a[0])};return;}}
        if(!provider.canWrite||active||gesture)return;const p=point(e);penDown=e.pointerType==='pen';
        if(tool.value==='select'){
            const text=getTexts().reverse().find(t=>p[0]>=t.x&&p[1]>=t.y&&p[0]<=t.x+Math.max(...t.text.split('\n').map(l=>l.length))*6*t.scale&&p[1]<=t.y+t.text.split('\n').length*8*t.scale);
            const [w,h]=pageSize(getMeta()),region=text?null:connectedRegion(raster,w,h,p[0],p[1]);
            selected=text?{textId:text.id,x:p[0],y:p[1],dx:0,dy:0,pointer:e.pointerId}:region?{region,ids:new Set([...region.owners].map(n=>ops.get(n-1)?.id)),x:p[0],y:p[1],dx:0,dy:0,pointer:e.pointerId}:null;draw();return;
        }
        if(tool.value==='text'){
            const existing=getTexts().reverse().find(t=>p[0]>=t.x&&p[1]>=t.y&&p[0]<=t.x+Math.max(1,...t.text.split('\n').map(l=>l.length))*6*t.scale&&p[1]<=t.y+t.text.split('\n').length*8*t.scale);
            if(existing){editText(texts.get(existing.id));return;}
            const id=crypto.randomUUID(),box=new Y.Map(),text=new Y.Text();provider.doc.transact(()=>{box.set('x',Math.round(p[0]));box.set('y',Math.round(p[1]));box.set('ink',ink.value);box.set('scale',2);box.set('text',text);texts.set(id,box);},local);editText(box);return;}
        active={id:crypto.randomUUID(),pointerId:e.pointerId,pointerType:e.pointerType,tool:tool.value,ink:ink.value,level:Number(level.value),seed:Math.floor(Math.random()*2147483647),points:[p],shift:e.shiftKey};updateAwareness(p);draw();
    }
    function move(e){if(e.pointerType==='touch'&&penDown)return;
        if(touches.has(e.pointerId))touches.set(e.pointerId,[e.clientX,e.clientY]);
        if(gesture&&touches.size>=2){const [a,b]=[...touches.values()];pan=[gesture.pan[0]+(a[0]+b[0])/2-gesture.mid[0],gesture.pan[1]+(a[1]+b[1])/2-gesture.mid[1]];zoom=Math.max(1,Math.min(8,Math.round(gesture.zoom*Math.hypot(a[0]-b[0],a[1]-b[1])/gesture.distance)));angle=gesture.angle+(Math.atan2(b[1]-a[1],b[0]-a[0])-gesture.rotation)*180/Math.PI;transform();return;}
        const p=point(e);if(selected?.pointer===e.pointerId){selected.dx=Math.round(p[0]-selected.x);selected.dy=Math.round(p[1]-selected.y);draw();return;}
        if(active?.pointerId===e.pointerId){for(const sample of e.getCoalescedEvents?.().length?e.getCoalescedEvents():[e])active.points.push(point(sample));active.shift=e.shiftKey;draw();}
        if(e.pointerType!=='touch')updateAwareness(p);
    }
    function up(e){touches.delete(e.pointerId);if(gesture){if(touches.size<2)gesture=null;return;}
        if(selected?.pointer===e.pointerId){if(provider.canWrite&&e.type!=='pointercancel'){const selection=selected,[w,h]=pageSize(getMeta());provider.doc.transact(()=>{
            if(selection.textId){const box=texts.get(selection.textId);box.set('x',box.get('x')+selection.dx);box.set('y',box.get('y')+selection.dy);}
            else {const moved=splitMove(ops.toArray(),selection.region,w,h,selection.dx,selection.dy,selection.ids);ops.delete(0,ops.length);ops.insert(0,moved);}
        },local);}selected=null;penDown=false;draw();return;}
        if(active?.pointerId!==e.pointerId)return;active.points.push(point(e));if(provider.canWrite&&e.type!=='pointercancel'){let stroke=active;if(stroke.tool==='rectangle'||stroke.tool==='ellipse')stroke={...stroke,tool:'pen',points:shape(stroke.tool,stroke.points[0],stroke.points.at(-1),constrain.checked||stroke.shift)};provider.doc.transact(()=>ops.push([stroke]),local);undo.stopCapturing();}
        active=null;penDown=false;provider.awareness.setLocalStateField('stroke',null);draw();
    }
    const block=e=>e.preventDefault();canvas.addEventListener('pointerdown',down);canvas.addEventListener('pointermove',move);canvas.addEventListener('pointerup',up);canvas.addEventListener('pointercancel',up);canvas.addEventListener('contextmenu',block);
    button('Undo',()=>undo.undo(),true);button('Redo',()=>undo.redo(),true);
    button('Clear',async()=>{if(provider.canWrite&&await bridge.invokeMethodAsync('Confirm','Clear this sketch? Undo can restore it.'))provider.doc.transact(()=>{ops.delete(0,ops.length);for(const key of texts.keys())texts.delete(key);},local);},true);
    button('Zoom +',()=>{zoom=Math.min(8,zoom+1);transform();});button('Zoom −',()=>{zoom=Math.max(1,zoom-1);transform();});
    button('Rotate view',()=>{angle=(angle+90)%360;transform();});button('Reset view',()=>{pan=[12,12];zoom=1;angle=0;transform();});
    button('Export PNG',()=>{const output=document.createElement('canvas');paint(output.getContext('2d'),ops.toArray(),getTexts(),getMeta(),{font,frame:0,exporting:true});output.toBlob(blob=>{if(!blob||disposed)return;const url=URL.createObjectURL(blob);downloads.push(url);const a=document.createElement('a');a.href=url;a.download=options.name+'.png';document.body.append(a);a.click();a.remove();},'image/png');});
    async function changeMeta(){const next={pageSize:paper.value,orientation:orientation.value,pattern:pattern.value},[w,h]=pageSize(next),[oldW,oldH]=pageSize(getMeta());
        if((w<oldW||h<oldH)&&(ops.length||texts.size)&&!await bridge.invokeMethodAsync('Confirm','Line work outside the new page bounds will be hidden. Enlarging the page brings it back.')){draw();return;}
        try{await bridge.invokeMethodAsync('Meta',next.pageSize,next.orientation,next.pattern);provider.doc.transact(()=>Object.entries(next).forEach(([k,v])=>meta.set(k,v)),local);}catch(e){say(e.message);draw();}
    }
    paper.onchange=changeMeta;orientation.onchange=changeMeta;pattern.onchange=changeMeta;
    draw();return {connected:value=>provider.connected(value),receive:(kind,data)=>provider.receive(kind,data),presence:users=>provider.presence(users),
        dispose(){disposed=true;clearInterval(boil);observer.disconnect();undo.destroy();provider.dispose();downloads.forEach(url=>URL.revokeObjectURL(url));canvas.removeEventListener('pointerdown',down);canvas.removeEventListener('pointermove',move);canvas.removeEventListener('pointerup',up);canvas.removeEventListener('pointercancel',up);canvas.removeEventListener('contextmenu',block);root.replaceChildren();}};
}
