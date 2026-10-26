export function moveStroke(doc,stroke,parts){
    const placement=crypto.randomUUID();
    doc.getMap('cuts').set(stroke.id,{...parts,placement});
    doc.getArray('ops').push([{id:placement,relocates:stroke.id}]);
    return parts.moved;
}
export function sketchStrokes(doc){
    const cuts=doc.getMap('cuts'),result=[];
    function append(stroke,seen){
        if(stroke.relocates){
            const cut=cuts.get(stroke.relocates);
            if(cut?.placement===stroke.id)for(const part of cut.moved)append(part,seen);
            return;
        }
        const replacement=cuts.get(stroke.id);
        if(!replacement||seen.has(stroke.id)){result.push(stroke);return;}
        const next=new Set(seen);next.add(stroke.id);for(const part of Array.isArray(replacement)?replacement:replacement.stationary)append(part,next);
    }
    for(const stroke of doc.getArray('ops'))append(stroke,new Set());
    return result;
}
