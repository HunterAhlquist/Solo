import { ViewPlugin, Decoration, WidgetType, EditorView } from '@codemirror/view';
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
    ignoreEvent() { return false; }
}
function build(view, bridge) {
    const ranges = [], doc = view.state.doc, cursorLines = new Set();
    if (view.state.facet(EditorView.editable)) for (const r of view.state.selection.ranges) for (let n=doc.lineAt(r.from).number;n<=doc.lineAt(r.to).number;n++) cursorLines.add(n);
    syntaxTree(view.state).iterate({enter(node) {
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
export const livePreview = bridge => ViewPlugin.fromClass(class {
    constructor(view) { this.decorations=build(view,bridge); }
    update(update) { if(update.docChanged||update.selectionSet||update.viewportChanged) this.decorations=build(update.view,bridge); }
}, {decorations:v=>v.decorations});
