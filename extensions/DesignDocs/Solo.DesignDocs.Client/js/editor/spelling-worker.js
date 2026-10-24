import aff from '../node_modules/dictionary-en/index.aff';
import dic from '../node_modules/dictionary-en/index.dic';
import { createSpeller } from './spelling-words.js';

const spell=createSpeller({aff,dic});let text='';
self.onmessage=({data})=>{
    if(data.kind==='check'){
        if(data.text!==undefined)text=data.text;
        self.postMessage({kind:'checked',id:data.id,words:spell.check(text,data.ranges)});
    }else if(data.kind==='suggest')self.postMessage({kind:'suggestions',id:data.id,words:spell.suggest(data.word)});
    else if(data.kind==='ignore')spell.ignore(data.word);
};
self.postMessage({kind:'ready'});
