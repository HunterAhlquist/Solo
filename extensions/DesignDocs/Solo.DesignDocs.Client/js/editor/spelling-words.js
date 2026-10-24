import nspell from 'nspell';
import { parser } from '@lezer/markdown';

export function createSpeller(dictionary){
    const spell=nspell(dictionary),cache=new Map();let previous,excluded=[];
    return {
        check(text,ranges=[{from:0,to:text.length}]){
            if(text!==previous){
                previous=text;excluded=[];
                parser.parse(text).iterate({enter(node){if(/^(FencedCode|CodeBlock|InlineCode|URL|Autolink|HTMLBlock|HTMLTag)$/.test(node.name)){excluded.push({from:node.from,to:node.to});return false;}}});
                for(const match of text.matchAll(/!?\[\[[^\]]+\]\]|https?:\/\/\S+|\b[A-Z][A-Z0-9]*-[A-Z][A-Z0-9]*-\d+\b/g))excluded.push({from:match.index,to:match.index+match[0].length});
                excluded.sort((a,b)=>a.from-b.from);
            }
            const result=[];
            for(const range of ranges){
                const content=text.slice(range.from,range.to);
                for(const match of content.matchAll(/\p{L}[\p{L}\p{M}]*(?:['’][\p{L}\p{M}]+)*/gu)){
                    const from=range.from+match.index,to=from+match[0].length,word=match[0];
                    if(word.length<2||word.length>48||word===word.toUpperCase()||/\p{Ll}\p{Lu}/u.test(word)||/[\p{L}\p{N}_]/u.test(text[from-1]??'')||/[\p{L}\p{N}_]/u.test(text[to]??'')||excluded.some(r=>from<r.to&&to>r.from))continue;
                    const normalized=word.replaceAll('’',"'");
                    if(!cache.has(normalized)){if(cache.size>=10000)cache.clear();cache.set(normalized,spell.correct(normalized));}
                    if(!cache.get(normalized))result.push({from,to,word});
                }
            }
            return result;
        },
        suggest:word=>spell.suggest(word.replaceAll('’',"'")).slice(0,6),
        ignore(word){spell.add(word.replaceAll('’',"'"));cache.clear();},
    };
}
