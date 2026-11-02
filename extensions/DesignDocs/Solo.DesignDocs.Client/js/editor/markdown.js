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
export function isSafeUrl(url) {
    const text = (url ?? '').replace(/^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$/g,''), colon = text.indexOf(':');
    return colon < 0 || /[/?#]/u.test(text.slice(0, colon)) || ['http','https','mailto'].includes(text.slice(0, colon).toLowerCase());
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
