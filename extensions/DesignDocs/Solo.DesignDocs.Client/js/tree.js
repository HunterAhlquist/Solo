// Native drag data is required by Safari/Firefox. A dedicated handle keeps
// selecting a note and scrolling the tree independent from moving its contents.
export function mountTree(root,bridge){
    let dragged=null,target=null,touch=null,ghost=null;
    const clearTarget=()=>{target?.classList.remove('is-drop-target');target=null;};
    const dropTarget=node=>{const found=node?.closest?.('[data-doc-drop]');return found&&root.contains(found)?found:null;};
    const highlight=node=>{const next=dropTarget(node);if(next!==target){clearTarget();target=next;target?.classList.add('is-drop-target');}};
    const finish=()=>{clearTarget();ghost?.remove();ghost=null;dragged=null;touch=null;};
    async function move(id,parent){try{await bridge.invokeMethodAsync('Move',id,parent||null);}finally{finish();}}
    function start(event){const handle=event.target.closest('[data-doc-tree-handle]');if(!handle)return;
        dragged=handle.closest('[data-doc-tree-id]').dataset.docTreeId;
        event.dataTransfer.setData('application/x-solo-design-doc-tree',dragged);event.dataTransfer.effectAllowed='move';
    }
    function over(event){if(!dragged)return;const destination=dropTarget(event.target);if(!destination)return;event.preventDefault();event.stopPropagation();event.dataTransfer.dropEffect='move';highlight(destination);}
    function drop(event){if(!dragged)return;const destination=dropTarget(event.target);if(!destination)return;event.preventDefault();event.stopPropagation();move(dragged,destination.dataset.docDrop);}
    function down(event){if(event.pointerType==='mouse'||!event.target.closest('[data-doc-tree-handle]'))return;
        event.preventDefault();event.stopPropagation();const row=event.target.closest('[data-doc-tree-id]');
        touch={id:event.pointerId,x:event.clientX,y:event.clientY,row,active:false};row.setPointerCapture(event.pointerId);
    }
    function pointerMove(event){if(touch?.id!==event.pointerId)return;event.preventDefault();
        if(!touch.active&&Math.hypot(event.clientX-touch.x,event.clientY-touch.y)>6){
            touch.active=true;dragged=touch.row.dataset.docTreeId;ghost=document.createElement('div');ghost.className='design-docs-tree-ghost';ghost.textContent=touch.row.dataset.docName;document.body.append(ghost);
        }
        if(!touch.active)return;
        ghost.style.left=`${event.clientX+12}px`;ghost.style.top=`${event.clientY+12}px`;highlight(document.elementFromPoint(event.clientX,event.clientY));
        const scroll=root.closest('.design-docs-tree'),bounds=scroll?.getBoundingClientRect();
        if(bounds){if(event.clientY<bounds.top+36)scroll.scrollTop-=12;else if(event.clientY>bounds.bottom-36)scroll.scrollTop+=12;}
    }
    function up(event){if(touch?.id!==event.pointerId)return;event.preventDefault();event.stopPropagation();if(event.type!=='pointercancel'&&touch.active&&target)move(dragged,target.dataset.docDrop);else finish();}
    root.addEventListener('dragstart',start);root.addEventListener('dragover',over);root.addEventListener('drop',drop);root.addEventListener('dragend',finish);
    root.addEventListener('pointerdown',down,true);root.addEventListener('pointermove',pointerMove);root.addEventListener('pointerup',up);root.addEventListener('pointercancel',up);
    return {dispose(){finish();root.removeEventListener('dragstart',start);root.removeEventListener('dragover',over);root.removeEventListener('drop',drop);root.removeEventListener('dragend',finish);root.removeEventListener('pointerdown',down,true);root.removeEventListener('pointermove',pointerMove);root.removeEventListener('pointerup',up);root.removeEventListener('pointercancel',up);}};
}
