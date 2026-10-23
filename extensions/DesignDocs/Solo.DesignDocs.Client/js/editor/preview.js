import { refreshSketchPreview } from '../media/sketch-preview.js';
export { refreshSketchPreview };
import { WikiLink } from './wiki.js';
import { StateField } from '@codemirror/state';
import { Decoration, WidgetType, EditorView } from '@codemirror/view';
import { syntaxTree } from '@codemirror/language';
import { isSafeUrl } from './markdown.js';
import { mediaType } from '../media/vault-paths.js';
class Link extends WidgetType {
    constructor(label, url, bridge) { super(); this.label = label; this.url = url; this.bridge=bridge; }
    eq(other) { return this.label === other.label && this.url === other.url; }
    toDOM() {
        const a = document.createElement('a'); a.textContent = this.label; a.href = this.url;
        const task = /^\/docs\/task\/([0-9a-f-]{32,36})$/i.exec(this.url);
        if(task) {
            a.className='design-docs-task-chip';
            const refresh=()=>this.bridge.invokeMethodAsync('TaskInfo',task[1]).then(info=>{a.textContent=`${info.serial} ${info.name}`;}).catch(()=>{a.textContent=this.label;});refresh();this.timer=setInterval(()=>{if(a.isConnected)refresh();},30000);
            a.addEventListener('click',event=>{event.preventDefault();this.bridge.invokeMethodAsync('OpenTask',task[1]);});
        } else if (/^https?:/i.test(this.url)) { a.target = '_blank'; a.rel = 'noopener noreferrer'; }
        return a;
    }
    ignoreEvent() { return true; }
    destroy(){clearInterval(this.timer);}
}
class MediaCard extends WidgetType {
    constructor(entries,images){super();this.entries=entries;this.images=images;}
    eq(other){return this.entries.length===other.entries.length&&this.entries.every((e,i)=>e.from===other.entries[i].from&&e.to===other.entries[i].to&&e.raw===other.entries[i].raw);}
    toDOM(){return this.images.card(this.entries);}
    ignoreEvent(){return true;}
    destroy(dom){this.images.release(dom);}
}
class ListMarker extends WidgetType {
    constructor(raw){super();this.raw=raw;}
    eq(other){return this.raw===other.raw;}
    toDOM(){const span=document.createElement('span'),ordered=/^\d/.test(this.raw);span.className='design-docs-list-marker'+(ordered?' is-ordered':'');span.textContent=ordered?this.raw:'•';return span;}
}
class UnsupportedMedia extends WidgetType {
    constructor(path,kind){super();this.path=path;this.kind=kind;}
    eq(other){return this.path===other.path&&this.kind===other.kind;}
    toDOM(){const label=document.createElement('span');label.className='design-docs-unsupported-media';label.textContent=(this.kind==='video'?'Video not imported: ':'Unsupported attachment: ')+this.path;return label;}
}
function build(view, bridge, images) {
    const ranges = [], doc = view.state.doc, cursorLines = new Set();
    if (view.state.facet(EditorView.editable)) for (const r of view.state.selection.ranges) for (let n=doc.lineAt(r.from).number;n<=doc.lineAt(r.to).number;n++) cursorLines.add(n);
    const tree=syntaxTree(view.state);
    const insideCode=from=>{for(let node=tree.resolveInner(from,1);node;node=node.parent)if(/^(FencedCode|CodeBlock|InlineCode)$/.test(node.name))return true;return false;};
    const embedded=[];
    for(let number=1;number<=doc.lines;number++) {
        const line=doc.line(number);
        const matches=[...line.text.matchAll(/!\[\[([^\]]+)\]\]|!\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)/g)]
            .filter(m=>!insideCode(line.from+m.index));
        if(!matches.length)continue;
        const onlyMedia=matches.reduce((text,m)=>text.replace(m[0],''),line.text).trim()===''&&matches.every(m=>['image','sketch'].includes(mediaType(m[1]??m[2]??m[3])));
        for(const group of onlyMedia?[matches]:matches.map(m=>[m])) {
            const entries=group.map(m=>{
                let path=(m[1]??m[2]??m[3]).split('|')[0];try{path=decodeURIComponent(path);}catch{}
                return {path,from:line.from+m.index,to:line.from+m.index+m[0].length,raw:m[0],sketch:/\.sketch$/i.test(path)};
            });
            const from=entries[0].from,to=entries.at(-1).to;
            const kind=mediaType(entries[0].path),supported=kind==='image'||kind==='sketch';
            if(!supported&&cursorLines.has(number))continue;
            ranges.push(Decoration.replace({widget:supported?new MediaCard(entries,images):new UnsupportedMedia(entries[0].path,kind),media:supported}).range(from,to));
            embedded.push({from,to});
        }
    }
    for(const match of doc.toString().matchAll(/\[\[([^\]]+)\]\]/g)){
        if(match.index>0&&doc.sliceString(match.index-1,match.index)==='!')continue;
        if(cursorLines.has(doc.lineAt(match.index).number))continue;
        let node=syntaxTree(view.state).resolveInner(match.index,1),code=false;for(;node;node=node.parent)if(/^(FencedCode|CodeBlock|InlineCode)$/.test(node.name)){code=true;break;}if(code)continue;
        const [target,label]=match[1].split('|',2),from=match.index,to=from+match[0].length;
        ranges.push(Decoration.replace({widget:new WikiLink(target,label??target,bridge)}).range(from,to));embedded.push({from,to});
    }
    syntaxTree(view.state).iterate({enter(node) {
        if(embedded.some(e=>node.from>=e.from&&node.to<=e.to))return false;
        const line = doc.lineAt(node.from);
        if (/^(ATX|Setext)Heading[1-6]$/.test(node.name)) ranges.push(Decoration.line({class:`design-docs-h${node.name.at(-1)}`}).range(line.from));
        if (node.name === 'StrongEmphasis') ranges.push(Decoration.mark({class:'design-docs-strong'}).range(node.from,node.to));
        if (node.name === 'Emphasis') ranges.push(Decoration.mark({class:'design-docs-emphasis'}).range(node.from,node.to));
        if (node.name === 'InlineCode') ranges.push(Decoration.mark({class:'design-docs-code'}).range(node.from,node.to));
        if(node.name==='ListMark'){
            const adjacent=view.state.facet(EditorView.editable)&&view.state.selection.ranges.some(range=>range.from<=node.to&&range.to>=node.from);
            if(!adjacent)ranges.push(Decoration.replace({widget:new ListMarker(doc.sliceString(node.from,node.to)),listMarker:true}).range(node.from,node.to));
        }
        if (cursorLines.has(line.number)) return;
        if (/^(HeaderMark|EmphasisMark|CodeMark|StrikethroughMark)$/.test(node.name)) {
            // An ATX marker's separator belongs to its syntax, not the rendered title.
            const separator=node.name==='HeaderMark'&&/^ATXHeading/.test(node.node.parent?.name??'')&&node.from===line.from
                ? /^[ \t]*/.exec(doc.sliceString(node.to,line.to))[0].length:0;
            ranges.push(Decoration.replace({}).range(node.from,node.to+separator));
        }
        if (node.name === 'Link' && doc.lineAt(node.to).number === line.number) {
            const raw = doc.sliceString(node.from,node.to), match = /^\[([^\]]+)\]\(([^\s)]+)\)$/.exec(raw);
            if (match && isSafeUrl(match[2])) { ranges.push(Decoration.replace({widget:(/^[^:#?]+\.md(?:#.*)?$/i.test(match[2])?new WikiLink(match[2],match[1],bridge):new Link(match[1],match[2],bridge))}).range(node.from,node.to)); return false; }
        }
    }});
    return Decoration.set(ranges, true);
}
export const livePreview = (bridge,images) => StateField.define({
    create:state=>build({state},bridge,images),
    update:(_,transaction)=>build({state:transaction.state},bridge,images),
    provide:field=>[
        EditorView.decorations.from(field),
        EditorView.atomicRanges.of(view=>view.state.field(field).update({filter:(_from,_to,value)=>!!value.spec.media})),
    ],
});
