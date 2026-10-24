import { StateEffect } from '@codemirror/state';
import { Decoration,EditorView,ViewPlugin } from '@codemirror/view';

export const needsSpellingFallback=platform=>/iPad|iPhone|iPod/.test(platform.userAgent??'')||platform.platform==='MacIntel'&&platform.maxTouchPoints>1;
const results=StateEffect.define();
export function mapSpellingTarget(target,changes){
    return target?{...target,from:changes.mapPos(target.from,1),to:changes.mapPos(target.to,-1)}:null;
}
export function spellingCorrection(state,target,replacement){
    return !state.readOnly&&state.facet(EditorView.editable)&&target&&target.from<=target.to&&target.from>=0&&target.to<=state.doc.length&&state.doc.sliceString(target.from,target.to)===target.word
        ?{changes:{from:target.from,to:target.to,insert:replacement},selection:{anchor:target.from+replacement.length},userEvent:'input.spelling'}:null;
}
export function localSpelling(bridge){
    return ViewPlugin.fromClass(class{
        constructor(view){
            this.view=view;this.decorations=Decoration.none;this.serial=0;this.generation=0;this.pending=new Map();
            try{
                const url=new URL('spelling-worker.js',import.meta.url);url.search=new URL(import.meta.url).search;
                this.worker=new Worker(url,{type:'module'});
                this.worker.onmessage=({data})=>{
                    if(this.disposed)return;
                    if(data.kind==='ready'){this.ready=true;this.schedule(0);}
                    else if(data.kind==='checked'&&data.id===this.generation&&this.editable())view.dispatch({effects:results.of(data.words)});
                    else if(data.kind==='suggestions'){this.pending.get(data.id)?.(data.words);this.pending.delete(data.id);}
                };
                this.worker.onerror=()=>this.fail();
            }catch{this.fail();}
        }
        editable(){return !this.failed&&!this.disposed&&!this.view.state.readOnly&&this.view.state.facet(EditorView.editable);}
        schedule(delay=450){
            clearTimeout(this.timer);this.generation=++this.serial;
            if(!this.ready||!this.editable())return;
            this.timer=setTimeout(()=>{
                const doc=this.view.state.doc,text=this.sentDoc===doc?undefined:doc.toString();this.sentDoc=doc;
                this.worker.postMessage({kind:'check',id:this.generation,text,ranges:this.view.visibleRanges});
            },delay);
        }
        update(update){
            this.decorations=this.decorations.map(update.changes);
            if(update.docChanged)this.target=mapSpellingTarget(this.target,update.changes);
            for(const effect of update.transactions.flatMap(t=>t.effects))if(effect.is(results))this.decorations=Decoration.set(effect.value.map(word=>Decoration.mark({class:'design-docs-spelling-error',word:word.word,attributes:{title:'English spelling: '+word.word}}).range(word.from,word.to)),true);
            if(!this.editable())this.decorations=Decoration.none;
            if(update.docChanged||update.viewportChanged||update.startState.readOnly!==update.state.readOnly||update.startState.facet(EditorView.editable)!==update.state.facet(EditorView.editable))this.schedule(update.docChanged?450:100);
        }
        async open(event){
            const span=event.target.closest('.design-docs-spelling-error');if(!span||!this.editable())return false;
            event.preventDefault();if(this.busy)return true;
            const position=this.view.posAtDOM(span);this.target=null;
            this.decorations.between(position,position,(from,to,mark)=>{if(mark.spec.word)this.target={from,to,word:mark.spec.word};});
            if(!this.target)return true;
            this.busy=true;
            try{
                const id=++this.serial,word=this.target.word;
                const suggestions=await new Promise(resolve=>{this.pending.set(id,resolve);this.worker.postMessage({kind:'suggest',id,word});});
                if(this.disposed||!this.editable()||!spellingCorrection(this.view.state,this.target,word))return true;
                const choice=await bridge.invokeMethodAsync('SpellingMenu',suggestions,event.clientX,event.clientY);
                if(this.disposed||!this.editable())return true;
                if(choice==='ignore'){this.worker.postMessage({kind:'ignore',word});this.schedule(0);}
                else if(choice!==null&&suggestions[Number(choice)]){
                    const change=spellingCorrection(this.view.state,this.target,suggestions[Number(choice)]);
                    if(change){this.view.dispatch(change);this.view.focus();}
                }
            }catch{if(!this.disposed)bridge.invokeMethodAsync('Notice','Spelling suggestions are unavailable.').catch(()=>{});}
            finally{this.busy=false;this.target=null;}
            return true;
        }
        fail(){
            if(this.failed||this.disposed)return;this.failed=true;this.ready=false;clearTimeout(this.timer);this.worker?.terminate();
            for(const resolve of this.pending.values())resolve([]);this.pending.clear();
            queueMicrotask(()=>{if(!this.disposed)this.view.dispatch({effects:results.of([])});});
            bridge.invokeMethodAsync('Notice','English spellcheck could not start. Reopen this note to retry.').catch(()=>{});
        }
        destroy(){this.disposed=true;clearTimeout(this.timer);this.worker?.terminate();for(const resolve of this.pending.values())resolve([]);this.pending.clear();}
    },{decorations:plugin=>plugin.decorations,eventHandlers:{
        click(event){if(event.target.closest('.design-docs-spelling-error')&&this.editable()){void this.open(event);return true;}return false;},
        contextmenu(event){if(event.target.closest('.design-docs-spelling-error')&&this.editable()){void this.open(event);return true;}return false;},
    }});
}
