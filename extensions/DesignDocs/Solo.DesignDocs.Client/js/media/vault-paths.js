import { parser } from '@lezer/markdown';
export const attachmentName=path=>path.split('/').at(-1);
export function mediaType(target){
    let path=String(target).split('|')[0].split(/[?#]/)[0];try{path=decodeURIComponent(path);}catch{}
    if(/\.sketch$/i.test(path))return 'sketch';
    if(/\.(png|jpe?g|webp|gif)$/i.test(path))return 'image';
    if(/\.(mp4|m4v|mov|webm|mkv|avi|mpeg|mpg|ogv|wmv|3gp|3g2|mts|m2ts|vob)$/i.test(path))return 'video';
    return 'unsupported';
}
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
// Image and sketch embeds outside code, with their raw targets and where they sit in the text.
export function embeds(markdown){
    const result=[],tree=parser.parse(markdown);for(const match of markdown.matchAll(/!\[\[([^\]]+)\]\]|!\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)/g)){
        let node=tree.resolveInner(match.index,1),code=false;for(;node;node=node.parent)if(/^(FencedCode|CodeBlock|InlineCode)$/.test(node.name)){code=true;break;}
        if(!code)result.push({raw:match[1]??match[2]??match[3],from:match.index,to:match.index+match[0].length});
    }return result;
}
export const imageTargets=markdown=>embeds(markdown).map(e=>e.raw);
