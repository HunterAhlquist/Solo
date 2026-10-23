import { parser } from '@lezer/markdown';
export function normalizePath(value){
    if(typeof value!=='string'||value.startsWith('/')||/^[a-z][a-z\d+.-]*:/i.test(value))return null;
    const result=[];for(const part of value.replaceAll('\\','/').split('/')){if(!part||part==='.')continue;if(part==='..'){if(!result.length)return null;result.pop();}else result.push(part);}return result.join('/');
}
export function resolvePath(paths,target,from=''){
    if(/^(?:[a-z][a-z\d+.-]*:|\/\/)/i.test(target))return null;
    let value;try{value=decodeURIComponent(target.split('|')[0].split('#')[0]);}catch{return null;}
    const exact=normalizePath(value);if(exact&&paths.includes(exact))return exact;
    const relative=normalizePath(from.split('/').slice(0,-1).concat(value).join('/'));if(relative&&paths.includes(relative))return relative;
    const name=value.split('/').at(-1),matches=paths.filter(path=>path.split('/').at(-1)===name);return matches.length===1?matches[0]:null;
}
export function imageTargets(markdown){
    const result=[],tree=parser.parse(markdown);for(const match of markdown.matchAll(/!\[\[([^\]]+)\]\]|!\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)/g)){
        let node=tree.resolveInner(match.index,1),code=false;for(;node;node=node.parent)if(/^(FencedCode|CodeBlock|InlineCode)$/.test(node.name)){code=true;break;}
        if(!code)result.push(match[1]??match[2]??match[3]);
    }return result;
}
