// Inline formatting for the floating panel: what the selection already has, and
// toggling a style on or off by editing the markdown around it.
import { EditorSelection } from '@codemirror/state';
import { syntaxTree } from '@codemirror/language';

// Delimiter syntax lezer doesn't know. `==` is the extended-markdown highlight
// (Obsidian, markdown-it-mark). Underline has no markdown syntax, so it is the
// `<u>` tag that Obsidian and GitHub both render.
const HighlightDelim={resolve:'Highlight',mark:'HighlightMark'};
const UnderlineDelim={resolve:'Underline',mark:'UnderlineMark'};
export const inlineSyntax=[{
    defineNodes:['Highlight','HighlightMark','Underline','UnderlineMark'],
    parseInline:[{
        name:'Highlight',
        parse(cx,next,pos){
            if(next!==61||cx.char(pos+1)!==61||cx.char(pos+2)===61||cx.char(pos-1)===61)return -1;
            const before=cx.slice(pos-1,pos),after=cx.slice(pos+2,pos+3);
            return cx.addDelimiter(HighlightDelim,pos,pos+2,!/\s|^$/.test(after),!/\s|^$/.test(before));
        },
        after:'Emphasis',
    },{
        name:'Underline',
        parse(cx,next,pos){
            if(next!==60)return -1;
            if(/^<u>$/i.test(cx.slice(pos,pos+3)))return cx.addDelimiter(UnderlineDelim,pos,pos+3,true,false);
            if(/^<\/u>$/i.test(cx.slice(pos,pos+4)))return cx.addDelimiter(UnderlineDelim,pos,pos+4,false,true);
            return -1;
        },
        before:'HTMLTag',
    }],
}];

const children=node=>{const list=[];for(let child=node.firstChild;child;child=child.nextSibling)list.push(child);return list;};
// Each style's opening and closing markup inside one of its nodes. A link's
// closing markup is everything after the label: `](url "title")`.
const wrapped=(open,close)=>node=>{const marks=children(node).filter(c=>c.name===open);return marks.length<2?null:[marks[0],marks.at(-1)];};
const STYLES={
    bold:{node:'StrongEmphasis',open:'**',close:'**',marks:wrapped('EmphasisMark')},
    italic:{node:'Emphasis',open:'*',close:'*',marks:wrapped('EmphasisMark')},
    underline:{node:'Underline',open:'<u>',close:'</u>',marks:wrapped('UnderlineMark')},
    highlight:{node:'Highlight',open:'==',close:'==',marks:wrapped('HighlightMark')},
    link:{node:'Link',open:'[',marks:(node,doc)=>{
        const marks=children(node).filter(c=>c.name==='LinkMark'),end=marks.find(m=>doc.sliceString(m.from,m.to)===']');
        return marks[0]&&end&&end.from>marks[0].from?[marks[0],{from:end.from,to:node.to}]:null;
    }},
};
export const FORMAT_KINDS=Object.keys(STYLES);

// Inline markup can't cross a paragraph, and block markup (list bullets,
// heading hashes, quote markers) must stay outside it, so a selection is
// formatted one line at a time, trimmed of whitespace and leading block markup.
const BLOCK_PREFIX=/^\s*(?:>\s*)*(?:(?:[-*+]|\d{1,9}[.)])\s+(?:\[[ xX]\]\s+)?)?(?:#{1,6}\s+)?/;
function segments(state,{from,to}){
    const result=[];
    for(let number=state.doc.lineAt(from).number;number<=state.doc.lineAt(to).number;number++){
        const line=state.doc.line(number);
        let start=Math.max(from,line.from+BLOCK_PREFIX.exec(line.text)[0].length),end=Math.min(to,line.to);
        while(start<end&&/\s/.test(state.doc.sliceString(start,start+1)))start++;
        while(end>start&&/\s/.test(state.doc.sliceString(end-1,end)))end--;
        if(start<end)result.push({from:start,to:end});
    }
    return result;
}
function enclosing(state,style,{from,to}){
    for(let node=syntaxTree(state).resolveInner(from,1);node;node=node.parent)
        if(node.name===style.node&&node.from<=from&&node.to>=to)return node;
    return null;
}
function overlapping(state,style,{from,to}){
    const nodes=[];
    syntaxTree(state).iterate({from,to,enter:node=>{if(node.name===style.node&&node.from<to&&node.to>from)nodes.push(node.node);}});
    return nodes;
}

/** Which styles cover all of the main selection, or null for a caret. */
export function formatState(state){
    const range=state.selection.main;if(range.empty)return null;
    const parts=segments(state,range);
    return Object.fromEntries(FORMAT_KINDS.map(kind=>[kind,parts.length>0&&parts.every(part=>!!enclosing(state,STYLES[kind],part))]));
}

/**
 * Toggles `kind` over the main selection. Off when every line of it already has
 * the style, on otherwise. `url` is the target for a new link.
 */
export function toggleFormat(state,kind,url){
    const style=STYLES[kind],range=state.selection.main,doc=state.doc;
    const parts=segments(state,range);if(!parts.length)return null;
    const on=!parts.every(part=>enclosing(state,style,part));
    const open=style.open,close=kind==='link'?`](${/[\s()<>]/.test(url)?`<${url}>`:url})`:style.close;
    const changes=[];
    for(const part of parts){
        const node=enclosing(state,style,part),marks=node&&style.marks(node,doc);
        if(on){
            if(node)continue;
            // Absorb any of the style that overlaps the selection, so bolding
            // `a **b** c` gives `**a b c**` rather than nested markers.
            let from=part.from,to=part.to;
            for(const other of overlapping(state,style,part)){
                const otherMarks=style.marks(other,doc);if(!otherMarks)continue;
                from=Math.min(from,other.from);to=Math.max(to,other.to);
                for(const mark of otherMarks)changes.push({from:mark.from,to:mark.to,insert:''});
            }
            changes.push({from,insert:open},{from:to,insert:close});
        }else if(marks){
            // Turning a style off inside a longer run splits the run, keeping
            // the existing markup (`__` or `*`, a link's own url) on either side.
            const [start,end]=marks,openText=doc.sliceString(start.from,start.to),closeText=doc.sliceString(end.from,end.to);
            changes.push(part.from<=start.to?{from:start.from,to:start.to,insert:''}:{from:part.from,insert:closeText});
            changes.push(part.to>=end.from?{from:end.from,to:end.to,insert:''}:{from:part.to,insert:openText});
        }
    }
    const unique=[...new Map(changes.map(c=>[`${c.from}:${c.to??''}:${c.insert}`,c])).values()];
    const set=state.changes(unique);
    // Inserted markup lands outside the selection, so the same text stays selected.
    const map=(pos,inner)=>set.mapPos(pos,inner);
    const forward=range.head>=range.anchor;
    const from=map(range.from,1),to=map(range.to,-1);
    return {changes:set,selection:EditorSelection.single(forward?from:to,forward?to:from),userEvent:'input.format'};
}
