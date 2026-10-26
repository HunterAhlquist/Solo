export function sketchStrokes(doc){
    const cuts=doc.getMap('cuts'),result=[];
    function append(stroke,seen){
        const replacement=cuts.get(stroke.id);
        if(!replacement||seen.has(stroke.id)){result.push(stroke);return;}
        const next=new Set(seen);next.add(stroke.id);for(const part of replacement)append(part,next);
    }
    for(const stroke of doc.getArray('ops'))append(stroke,new Set());
    return result;
}
