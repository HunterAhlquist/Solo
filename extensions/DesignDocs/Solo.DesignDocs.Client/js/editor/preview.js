import { refreshSketchPreview } from '../media/sketch-preview.js';
export { refreshSketchPreview };
import { WikiLink } from './wiki.js';
import { StateField } from '@codemirror/state';
import { Decoration, WidgetType, EditorView } from '@codemirror/view';
import { syntaxTree } from '@codemirror/language';
import { isSafeUrl, taskBox, inCode } from './markdown.js';
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
// The change that ticks or unticks a task. `pos` is where its drawn box starts in the text: the list mark of a
// bulleted task, the opening bracket of a numbered one. No selection is given, so the caret stays where it is.
export function taskToggle(state,pos) {
    if(state.readOnly)return null;
    const match=/^(?:[-+*][ \t]+)?\[([ xX])\](?=[ \t])/.exec(state.doc.sliceString(pos,state.doc.lineAt(pos).to));
    if(!match)return null;
    const at=pos+match[0].length-2;
    return {changes:{from:at,to:at+1,insert:match[1]===' '?'x':' '},userEvent:'input'};
}
class TaskBox extends WidgetType {
    constructor(checked,locked){super();this.checked=checked;this.locked=locked;}
    eq(other){return this.checked===other.checked&&this.locked===other.locked;}
    toDOM(view){
        const box=document.createElement('span');box.className='design-docs-task-box'+(this.checked?' is-checked':'');
        box.setAttribute('role','checkbox');box.setAttribute('aria-checked',String(this.checked));if(this.locked)box.setAttribute('aria-disabled','true');
        // A press would otherwise put the caret beside the box, which turns it back into its text before the click lands.
        box.addEventListener('mousedown',event=>event.preventDefault());
        // The box may have moved since it was drawn, so its place is read from the DOM rather than remembered.
        box.addEventListener('click',()=>{const spec=taskToggle(view.state,view.posAtDOM(box));if(spec)view.dispatch(spec);});
        return box;
    }
    ignoreEvent(){return true;}
}
class UnsupportedMedia extends WidgetType {
    constructor(path,kind){super();this.path=path;this.kind=kind;}
    eq(other){return this.path===other.path&&this.kind===other.kind;}
    toDOM(){const label=document.createElement('span');label.className='design-docs-unsupported-media';label.textContent=(this.kind==='video'?'Video not imported: ':'Unsupported attachment: ')+this.path;return label;}
}
function build(view, bridge, images) {
    const ranges = [], doc = view.state.doc, cursorLines = new Set();
    if (view.state.facet(EditorView.editable)) for (const r of view.state.selection.ranges) for (let n=doc.lineAt(r.from).number;n<=doc.lineAt(r.to).number;n++) cursorLines.add(n);
    const touches=(from,to)=>view.state.facet(EditorView.editable)&&view.state.selection.ranges.some(r=>r.from<=to&&r.to>=from);
    const tree=syntaxTree(view.state);
    const insideCode=from=>inCode(tree,from);
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
        if(view.state.facet(EditorView.editable)&&view.state.selection.ranges.some(r=>r.from<=match.index+match[0].length&&r.to>=match.index))continue;
        let node=syntaxTree(view.state).resolveInner(match.index,1),code=false;for(;node;node=node.parent)if(/^(FencedCode|CodeBlock|InlineCode)$/.test(node.name)){code=true;break;}if(code)continue;
        const [target,label]=match[1].split('|',2),from=match.index,to=from+match[0].length;
        ranges.push(Decoration.replace({widget:new WikiLink(target,label??target,bridge)}).range(from,to));embedded.push({from,to});
    }
    const quoteDepth=new Map();
    syntaxTree(view.state).iterate({enter(node) {
        if(embedded.some(e=>node.from>=e.from&&node.to<=e.to))return false;
        const line = doc.lineAt(node.from);
        // Every line of a quote gets a bar, lazy continuation lines (no '>') included.
        if(node.name==='Blockquote')for(let n=line.number;n<=doc.lineAt(node.to).number;n++)quoteDepth.set(n,(quoteDepth.get(n)??0)+1);
        if(node.name==='QuoteMark'&&!cursorLines.has(line.number)){
            const separator=/^[ \t]?/.exec(doc.sliceString(node.to,line.to))[0].length;
            ranges.push(Decoration.replace({}).range(node.from,node.to+separator));
        }
        if (/^(ATX|Setext)Heading[1-6]$/.test(node.name)) ranges.push(Decoration.line({class:`design-docs-h${node.name.at(-1)}`}).range(line.from));
        if (node.name === 'StrongEmphasis') ranges.push(Decoration.mark({class:'design-docs-strong'}).range(node.from,node.to));
        if (node.name === 'Emphasis') ranges.push(Decoration.mark({class:'design-docs-emphasis'}).range(node.from,node.to));
        if (node.name === 'InlineCode') ranges.push(Decoration.mark({class:'design-docs-code'}).range(node.from,node.to));
        if (node.name === 'Highlight') ranges.push(Decoration.mark({class:'design-docs-highlight'}).range(node.from,node.to));
        if(node.name==='ListMark'){
            const task=taskBox(doc.sliceString(node.to,line.to));
            if(task&&!insideCode(node.to+task.from)){
                // A task is a box in place of its list mark and brackets. The caret anywhere in what is written, up
                // to the closing bracket but not the space after it, shows the text so it can be changed by hand.
                const box={from:node.to+task.from,to:node.to+task.to};
                if(touches(node.from,box.to))return;
                const widget=new TaskBox(task.checked,view.state.readOnly),ordered=/^\d/.test(doc.sliceString(node.from,node.to));
                // A numbered task keeps its number; a bulleted one has the box for a bullet.
                if(ordered)ranges.push(Decoration.replace({widget:new ListMarker(doc.sliceString(node.from,node.to)),listMarker:true}).range(node.from,node.to));
                ranges.push(Decoration.replace({widget,task:true}).range(ordered?box.from:node.from,box.to));
                return;
            }
            const adjacent=view.state.facet(EditorView.editable)&&view.state.selection.ranges.some(range=>range.from<=node.to&&range.to>=node.from);
            if(!adjacent)ranges.push(Decoration.replace({widget:new ListMarker(doc.sliceString(node.from,node.to)),listMarker:true}).range(node.from,node.to));
        }
        if (node.name === 'Link' && doc.lineAt(node.to).number === line.number && !(view.state.facet(EditorView.editable)&&view.state.selection.ranges.some(r=>r.from<=node.to&&r.to>=node.from))) {
            const raw=doc.sliceString(node.from,node.to),match=/^\[((?:\\.|[^\]\\])+)\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)$/.exec(raw);
            const url=match?.[2]??match?.[3],label=match?.[1].replace(/\\([\\[\]])/g,'$1');
            if (match && isSafeUrl(url)) { ranges.push(Decoration.replace({widget:(/^[^:#?]+\.md(?:#.*)?$/i.test(url)?new WikiLink(url,label,bridge):new Link(label,url,bridge))}).range(node.from,node.to)); return false; }
        }
        if (/^(HeaderMark|EmphasisMark|CodeMark|StrikethroughMark|HighlightMark)$/.test(node.name)) {
            // Inline markup shows only while the cursor touches its own span, so
            // the rest of the line keeps rendering; headings and fences go by line.
            const span=node.node.parent;
            const inline=span&&/^(Emphasis|StrongEmphasis|InlineCode|Strikethrough|Highlight)$/.test(span.name);
            if(inline?touches(span.from,span.to):cursorLines.has(line.number))return;
            // An ATX marker's separator belongs to its syntax, not the rendered title.
            const separator=node.name==='HeaderMark'&&/^ATXHeading/.test(node.node.parent?.name??'')&&node.from===line.from
                ? /^[ \t]*/.exec(doc.sliceString(node.to,line.to))[0].length:0;
            ranges.push(Decoration.replace({}).range(node.from,node.to+separator));
        }
    }});
    for(const [number,depth] of quoteDepth)ranges.push(Decoration.line({class:'design-docs-quote',attributes:{style:`--design-docs-quote-depth:${depth}`}}).range(doc.line(number).from));
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
