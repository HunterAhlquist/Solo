import { StateField } from '@codemirror/state';
import { Decoration, WidgetType, EditorView } from '@codemirror/view';
import { syntaxTree } from '@codemirror/language';
import { isSafeUrl } from './markdown.js';
class Link extends WidgetType {
    constructor(label, url, bridge) { super(); this.label = label; this.url = url; this.bridge=bridge; }
    eq(other) { return this.label === other.label && this.url === other.url; }
    toDOM() {
        const a = document.createElement('a'); a.textContent = this.label; a.href = this.url;
        const task = /^\/docs\/task\/([0-9a-f-]{32,36})$/i.exec(this.url);
        if(task) {
            a.className='design-docs-task-chip';
            this.bridge.invokeMethodAsync('TaskInfo',task[1]).then(info=>{a.textContent=`${info.serial} ${info.name}`;}).catch(()=>{});
            a.addEventListener('click',event=>{event.preventDefault();this.bridge.invokeMethodAsync('OpenTask',task[1]);});
        } else if (/^https?:/i.test(this.url)) { a.target = '_blank'; a.rel = 'noopener noreferrer'; }
        return a;
    }
    ignoreEvent() { return true; }
}
class ImageCard extends WidgetType {
    constructor(paths,images){super();this.paths=paths;this.images=images;}
    eq(other){return this.paths.join('\n')===other.paths.join('\n');}
    toDOM(){return this.images.card(this.paths);}
    ignoreEvent(){return true;}
}
function build(view, bridge, images) {
    const ranges = [], doc = view.state.doc, cursorLines = new Set();
    if (view.state.facet(EditorView.editable)) for (const r of view.state.selection.ranges) for (let n=doc.lineAt(r.from).number;n<=doc.lineAt(r.to).number;n++) cursorLines.add(n);
    const embedded=[];let offset=0;
    for(const paragraph of doc.toString().split(/(\n\s*\n)/)) {
        const start=offset;offset+=paragraph.length;
        const matches=[...paragraph.matchAll(/!\[\[([^\]]+)\]\]|!\[[^\]]*\]\(([^\s)]+)\)/g)].filter(m=>!(/\.sketch$/i.test(m[1]??m[2])));
        if(!matches.length||matches.some(m=>cursorLines.has(doc.lineAt(start+m.index).number)))continue;
        const onlyImages=paragraph.replace(/!\[\[([^\]]+)\]\]|!\[[^\]]*\]\(([^\s)]+)\)/g,'').trim()==='';
        const groups=onlyImages?[matches]:matches.map(m=>[m]);
        for(const group of groups){const from=start+group[0].index,to=start+group.at(-1).index+group.at(-1)[0].length;
            const paths=group.map(m=>{try{return decodeURIComponent((m[1]??m[2]).split('|')[0]);}catch{return m[1]??m[2];}});
            ranges.push(Decoration.replace({widget:new ImageCard(paths,images)}).range(from,to));embedded.push({from,to});}
    }
    syntaxTree(view.state).iterate({enter(node) {
        if(embedded.some(e=>node.from>=e.from&&node.to<=e.to))return false;
        const line = doc.lineAt(node.from); if (cursorLines.has(line.number)) return;
        if (/^(HeaderMark|EmphasisMark|CodeMark|StrikethroughMark)$/.test(node.name)) ranges.push(Decoration.replace({}).range(node.from,node.to));
        if (/^ATXHeading[1-6]$/.test(node.name)) ranges.push(Decoration.line({class:`design-docs-h${node.name.at(-1)}`}).range(line.from));
        if (node.name === 'Link' && doc.lineAt(node.to).number === line.number) {
            const raw = doc.sliceString(node.from,node.to), match = /^\[([^\]]+)\]\(([^\s)]+)\)$/.exec(raw);
            if (match && isSafeUrl(match[2])) { ranges.push(Decoration.replace({widget:new Link(match[1],match[2],bridge)}).range(node.from,node.to)); return false; }
        }
        if (node.name === 'StrongEmphasis') ranges.push(Decoration.mark({class:'design-docs-strong'}).range(node.from,node.to));
        if (node.name === 'Emphasis') ranges.push(Decoration.mark({class:'design-docs-emphasis'}).range(node.from,node.to));
    }});
    return Decoration.set(ranges, true);
}
export const livePreview = (bridge,images) => StateField.define({
    create:state=>build({state},bridge,images),
    update:(_,transaction)=>build({state:transaction.state},bridge,images),
    provide:field=>EditorView.decorations.from(field),
});
