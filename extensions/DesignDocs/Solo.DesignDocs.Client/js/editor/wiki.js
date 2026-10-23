import { WidgetType } from '@codemirror/view';
import { headings } from './markdown.js';
export class WikiLink extends WidgetType {
    constructor(target,label,bridge){super();this.target=target;this.label=label;this.bridge=bridge;}
    eq(other){return this.target===other.target&&this.label===other.label;}
    toDOM(){const link=document.createElement('a');link.textContent=this.label;link.className='design-docs-wiki-link';
        const [path,fragment]=this.target.split('#',2);
        this.bridge.invokeMethodAsync('ResolveWiki',path).then(result=>{
            if(!result){link.classList.add('is-unresolved');link.title='Unresolved note';return;}
            const match=result.headings.find(h=>h.slug===fragment||h.text.toLowerCase()===fragment?.toLowerCase());
            const slug=fragment?(match?.slug??headings('# '+fragment)[0]?.slug):'';link.href=result.url+(slug?'#'+slug:'');
        }).catch(()=>{link.classList.add('is-unresolved');link.title='Unresolved note';});return link;
    }
    ignoreEvent(){return true;}
}
