import {TreeDragIntent} from './tree-intent.js';
import {mountTitleTicker} from './tree-ticker.js';
export function mountTree(root,bridge){
    let target=null,ghost=null,frame=null,suppressClickUntil=0,disposed=false;
    const clearTarget=()=>{target?.classList.remove('is-drop-target');target=null;};
    const dropTarget=node=>{const found=node?.closest?.('[data-doc-drop]');return found&&root.contains(found)?found:null;};
    const highlight=node=>{const next=dropTarget(node);if(next!==target){clearTarget();target=next;target?.classList.add('is-drop-target');}};
    const intent=new TreeDragIntent(press=>{
        ghost=document.createElement('div');ghost.className='design-docs-tree-ghost';ghost.textContent=press.item.dataset.docName;document.body.append(ghost);
        press.item.classList.add('is-dragging');render();
    });
    const ticker=mountTitleTicker(root,()=>!!intent.press);
    function render(){
        const press=intent.press;if(!press?.active||disposed)return;
        const [x,y]=press.point;ghost.style.left=(x+12)+'px';ghost.style.top=(y+12)+'px';highlight(document.elementFromPoint(x,y));
        const scroll=root.closest('.design-docs-tree'),bounds=scroll?.getBoundingClientRect();
        if(bounds){if(y<bounds.top+36)scroll.scrollTop-=12;else if(y>bounds.bottom-36)scroll.scrollTop+=12;}
        frame=requestAnimationFrame(render);
    }
    function detach(){document.removeEventListener('touchmove',refuseScroll);window.removeEventListener('pointermove',pointerMove);window.removeEventListener('pointerup',up);window.removeEventListener('pointercancel',up);}
    function finish(){const press=intent.finish();press?.item.classList.remove('is-dragging');if(frame!==null)cancelAnimationFrame(frame);frame=null;clearTarget();ghost?.remove();ghost=null;detach();return press;}
    function down(event){
        if(intent.press||event.isPrimary===false||event.button!==0)return;
        const title=event.target.closest?.('[data-doc-drag]');if(!title||!root.contains(title))return;
        ticker.stop();
        intent.begin(event,title.closest('[data-doc-tree-id]'));
        document.addEventListener('touchmove',refuseScroll,{passive:false});window.addEventListener('pointermove',pointerMove);window.addEventListener('pointerup',up);window.addEventListener('pointercancel',up);
    }
    function pointerMove(event){if(intent.press?.id!==event.pointerId)return;intent.move(event);if(!intent.press)detach();else if(intent.press.active)event.preventDefault();}
    function refuseScroll(event){if(intent.press?.active)event.preventDefault();}
    function up(event){
        const press=intent.press;if(!press||press.id!==event.pointerId)return;
        const destination=press.active&&event.type!=='pointercancel'?dropTarget(document.elementFromPoint(event.clientX,event.clientY)):null;
        const id=press.item.dataset.docTreeId,parent=destination?.dataset.docDrop;
        if(press.active){event.preventDefault();suppressClickUntil=Date.now()+400;}
        const editor=press.active&&event.type!=='pointercancel'&&press.item.dataset.docNote
            ?document.elementFromPoint(event.clientX,event.clientY)?.closest('.design-docs-editor'):null;
        const detail={x:event.clientX,y:event.clientY,url:press.item.dataset.docNote,label:press.item.dataset.docName};
        finish();if(editor)editor.dispatchEvent(new CustomEvent('solo-note-drop',{detail}));else if(destination)bridge.invokeMethodAsync('Move',id,parent||null);
    }
    function click(event){if(Date.now()<suppressClickUntil){suppressClickUntil=0;event.preventDefault();event.stopPropagation();}}
    function context(event){if(intent.press){event.preventDefault();event.stopPropagation();}}
    const blockNative=event=>{if(event.target.closest?.('[data-doc-drag]'))event.preventDefault();};
    root.addEventListener('pointerdown',down,true);root.addEventListener('click',click,true);root.addEventListener('contextmenu',context,true);root.addEventListener('dragstart',blockNative);
    return {dispose(){disposed=true;finish();ticker.dispose();root.removeEventListener('pointerdown',down,true);root.removeEventListener('click',click,true);root.removeEventListener('contextmenu',context,true);root.removeEventListener('dragstart',blockNative);}};
}
