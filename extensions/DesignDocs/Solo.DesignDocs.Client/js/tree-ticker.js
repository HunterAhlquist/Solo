// Match Roadmaps' hover ticker without taking over a title's click or drag.
export function mountTitleTicker(root,blocked){
    const reducedMotion=window.matchMedia?.('(prefers-reduced-motion: reduce)');
    let ticking=null;
    function stop(){if(ticking){delete ticking.dataset.ticker;ticking=null;}}
    function over(event){
        const label=blocked()||event.pointerType==='touch'?null:event.target.closest?.('.design-docs-tree-name');
        if(label===ticking)return;
        stop();if(!label||!root.contains(label)||reducedMotion?.matches)return;
        label.dataset.ticker='';
        const distance=label.querySelector('.design-docs-tree-text').getBoundingClientRect().width;
        const room=label.clientWidth-12;
        if(distance-32<=room+0.5){delete label.dataset.ticker;return;}
        label.style.setProperty('--design-docs-ticker-distance',`${distance}px`);
        label.style.setProperty('--design-docs-ticker-duration',`${distance/45}s`);
        ticking=label;
    }
    root.addEventListener('pointerover',over);root.addEventListener('pointerleave',stop);root.addEventListener('focusin',over);root.addEventListener('focusout',stop);
    return {stop,dispose(){stop();root.removeEventListener('pointerover',over);root.removeEventListener('pointerleave',stop);root.removeEventListener('focusin',over);root.removeEventListener('focusout',stop);}};
}
