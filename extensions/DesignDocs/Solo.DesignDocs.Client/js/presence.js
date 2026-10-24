export function presenceTheme(root){
    const scope=root.closest('.design-docs-sketch-dialog')??root.closest('.design-docs-page')??root;
    const probe=document.createElement('span');probe.style.color='var(--mud-palette-background, white)';root.append(probe);
    const rgb=getComputedStyle(probe).color.match(/[\d.]+/g)?.slice(0,3).map(Number)??[255,255,255];probe.remove();
    scope.style.setProperty('--design-docs-presence-lightness',rgb.reduce((a,b)=>a+b,0)<384?'75%':'50%');
}
export function presenceAvatar(user){
    const avatar=document.createElement('span');avatar.className='design-docs-cursor-avatar';avatar.setAttribute('aria-hidden','true');
    const words=((user.name??'').trim()||'?').split(/\s+/);avatar.textContent=(words.length>1?words[0][0]+words.at(-1)[0]:words[0].slice(0,1)).toUpperCase();
    if(user.avatarUrl&&/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(user.userId??'')){const image=document.createElement('img');image.src=`api/users/${user.userId}/avatar`;image.alt='';image.referrerPolicy='no-referrer';image.addEventListener('error',()=>image.remove(),{once:true});avatar.append(image);}
    return avatar;
}
// The arrow edges meet the profile circle at their tangent points.
export function pointerOutline(tip=[2,2],center=[23,27],radius=11){
    const [vx,vy]=[tip[0]-center[0],tip[1]-center[1]],distance=vx*vx+vy*vy;
    if(distance<=radius*radius)throw new RangeError('The cursor tip must lie outside the profile circle.');
    const along=radius*radius/distance,across=radius*Math.sqrt(distance-radius*radius)/distance;
    const first=[center[0]+along*vx-across*vy,center[1]+along*vy+across*vx];
    const second=[center[0]+along*vx+across*vy,center[1]+along*vy-across*vx];
    return {tip,center,radius,tangents:[first,second],path:`M${tip.join(' ')} L${first.join(' ')} A${radius} ${radius} 0 0 0 ${second.join(' ')} Z`};
}
export function presenceCursor(kind,user){
    const cursor=document.createElement('span');cursor.className='design-docs-cursor design-docs-cursor--'+kind;
    cursor.style.setProperty('--design-docs-user-color',user.color??'#a252d6');cursor.setAttribute('aria-label',user.name??'Collaborator');cursor.title=user.name??'Collaborator';
    const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.setAttribute('viewBox',kind==='text'?'0 0 12 24':'0 0 36 40');svg.setAttribute('aria-hidden','true');
    const outline=kind==='text'?'M2 2 Q6 4 10 2 M6 3 V21 M2 22 Q6 20 10 22':pointerOutline().path;
    const path=document.createElementNS('http://www.w3.org/2000/svg','path');path.setAttribute('d',outline);svg.append(path);
    if(kind==='text'){
        const body=document.createElementNS('http://www.w3.org/2000/svg','path');body.setAttribute('d',outline);body.classList.add('design-docs-caret-body');svg.append(body);
    }
    cursor.append(svg,presenceAvatar(user));return cursor;
}
export function viewportPoint(view,point){const a=view.angle*Math.PI/180;return [view.pan[0]+(Math.cos(a)*point[0]-Math.sin(a)*point[1])*view.zoom,view.pan[1]+(Math.sin(a)*point[0]+Math.cos(a)*point[1])*view.zoom];}
export class SketchPresence {
    constructor(viewport){this.layer=document.createElement('div');this.layer.className='design-docs-sketch-presence-layer';this.layer.setAttribute('aria-hidden','true');viewport.append(this.layer);this.markers=new Map();}
    render(states,view){
        const held=new Set();
        for(const [id,state]of states){
            const p=state.pointer;if(!p||!Number.isFinite(p.x)||!Number.isFinite(p.y)||!state.user)continue;
            held.add(id);let entry=this.markers.get(id);const key=JSON.stringify(state.user);
            if(!entry||entry.key!==key){entry?.root.remove();const root=document.createElement('span');root.className='design-docs-sketch-peer';const ring=document.createElement('span');ring.className='design-docs-brush-footprint';root.append(ring,presenceCursor('pointer',state.user));this.layer.append(root);entry={root,ring,key};this.markers.set(id,entry);}
            const [x,y]=viewportPoint(view,[p.x,p.y]);entry.root.style.transform=`translate(${x}px,${y}px)`;entry.root.style.setProperty('--design-docs-user-color',state.user.color??'#a252d6');
            const size=Math.max(2,Math.min(256,(p.width??1)*view.zoom));entry.ring.hidden=!['pen','eraser'].includes(p.tool);entry.ring.style.width=entry.ring.style.height=size+'px';
        }
        for(const [id,entry]of this.markers)if(!held.has(id)){entry.root.remove();this.markers.delete(id);}
    }
    dispose(){this.markers.clear();this.layer.remove();}
}
