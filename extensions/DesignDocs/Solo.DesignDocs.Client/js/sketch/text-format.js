// What the formatting panel shows for the current selection, and what the ink
// palette does with it.
//
// The rule throughout is that a value shared by every character in the range is
// reported, and a range that mixes values reports null. The panel draws null as
// "no answer" — an unlit B, an unselected swatch — which is what lets the ink
// palette honestly say "this selection is not one colour" rather than picking
// one of them.
export const FORMAT_KEYS=['bold','italic','underline','size','ink'];

// Boolean attributes are absent rather than false in a delta, so a run that
// never set one reads as off. Size and ink have no boolean default: unset means
// "whatever the box says", which the caller supplies.
const OFF={bold:false,italic:false,underline:false};

// A caret is not a range, but the panel still has to show something — and
// pressing B with no selection should set the format that the next character
// will be typed in. Editors answer that from the character *before* the caret,
// falling back to the one after it at the start of the text.
export function caretRange(start,end,length){
    if(start!==end)return [start,end];
    if(length===0)return [0,0];
    return start>0?[start-1,start]:[0,1];
}

// `delta` is a Y.Text delta: [{insert, attributes?}]. `defaults` carries the
// box-level size and ink that an unset run inherits.
export function rangeAttributes(delta,start,end,defaults={}){
    const fallback={...OFF,size:defaults.size,ink:defaults.ink};
    const [from,to]=caretRange(start,end,delta.reduce((n,part)=>n+String(part.insert??'').length,0));

    let index=0,found=false;
    const shared={};
    for(const part of delta){
        const text=String(part.insert??''),begin=index,finish=index+text.length;
        index=finish;
        // Half-open, so a run touching only the boundary is not in the range.
        // An empty range (empty text) matches nothing and reports the defaults.
        if(finish<=from||begin>=to||text.length===0)continue;

        const attributes=part.attributes??{};
        for(const key of FORMAT_KEYS){
            const value=attributes[key]??fallback[key];
            if(!found)shared[key]=value;
            else if(shared[key]!==value)shared[key]=null;
        }
        found=true;
    }

    return found?shared:{...fallback};
}
