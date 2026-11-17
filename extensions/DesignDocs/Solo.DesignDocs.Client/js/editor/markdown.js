import { parser as commonmark } from '@lezer/markdown';
// `==` is the extended-markdown highlight (Obsidian, markdown-it-mark), which lezer doesn't know.
const HighlightDelim={resolve:'Highlight',mark:'HighlightMark'};
export const inlineSyntax=[{
    defineNodes:['Highlight','HighlightMark'],
    parseInline:[{
        name:'Highlight',
        parse(cx,next,pos){
            if(next!==61||cx.char(pos+1)!==61||cx.char(pos+2)===61||cx.char(pos-1)===61)return -1;
            const before=cx.slice(pos-1,pos),after=cx.slice(pos+2,pos+3);
            return cx.addDelimiter(HighlightDelim,pos,pos+2,!/\s|^$/.test(after),!/\s|^$/.test(before));
        },
        after:'Emphasis',
    }],
}];

const parser=commonmark.configure(inlineSyntax);
// Trimmed by index: a `[…]+$` pattern retries at every position, which is
// quadratic on a long run of inner spaces.
const SPACE=/[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]/;
function trimSpace(value){
    let start=0,end=value.length;
    while(start<end&&SPACE.test(value[start]))start++;
    while(end>start&&SPACE.test(value[end-1]))end--;
    return value.slice(start,end);
}
export function isSafeUrl(url) {
    const text = trimSpace(url ?? ''), colon = text.indexOf(':');
    return colon < 0 || /[/?#]/u.test(text.slice(0, colon)) || ['http','https','mailto'].includes(text.slice(0, colon).toLowerCase());
}
// A task's box, looked for in what follows a list mark on its line: `[ ]`, `[x]` or `[X]` with white space on both
// sides, as GFM has it. Found by pattern because the editor's parser is CommonMark, which has no task node.
// Offsets are into `rest`. An item indented far enough to be a code block matches too, so callers check inCode.
export function taskBox(rest) {
    const match=/^([ \t]+)\[([ xX])\](?=[ \t])/.exec(rest);
    return match&&{from:match[1].length,to:match[0].length,checked:match[2]!==' '};
}
export function inCode(tree,pos) {
    for(let node=tree.resolveInner(pos,1);node;node=node.parent)if(/^(FencedCode|CodeBlock|InlineCode)$/.test(node.name))return true;
    return false;
}
export function headings(markdown) {
    const used=new Map(),result=[];
    parser.parse(markdown).iterate({enter(node){
        const type=/^(ATX|Setext)Heading([1-6])$/.exec(node.name);if(!type)return;
        // Highlight markup goes by node, not by pattern, so a literal `==` in a heading is left alone.
        let raw='',at=node.from;
        node.node.cursor().iterate(inner=>{if(inner.name==='HighlightMark'){raw+=markdown.slice(at,inner.from);at=inner.to;}});
        raw+=markdown.slice(at,node.to);
        let text=type[1]==='ATX'?raw.replace(/^\s{0,3}#{1,6}\s+/,'').replace(/\s+#+\s*$/,''):raw.slice(0,raw.lastIndexOf('\n'));
        text=text.replace(/\[([^\]]+)\]\([^)]+\)/g,'$1').replace(/[*_`]/g,'').replace(/\s+/g,' ').trim();
        const base=text.toLowerCase().replace(/\s+/gu,'-').replace(/[^\p{L}\p{N}_-]/gu,'');
        const count=(used.get(base)??0)+1;used.set(base,count);result.push({slug:base+(count>1?`-${count}`:''),text,level:Number(type[2]),from:node.from});
    }});return result;
}
