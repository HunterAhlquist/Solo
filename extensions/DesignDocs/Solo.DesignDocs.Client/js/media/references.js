import * as Y from 'yjs';

// A viewer can stay open while collaborators type before its embeds. Relative
// positions keep Remove and drag/drop attached to that occurrence, even when
// the same image appears several times in a note.
export function mediaReference(text, entry) {
    return {...entry, start:Y.createRelativePositionFromTypeIndex(text,entry.from,0), end:Y.createRelativePositionFromTypeIndex(text,entry.to,-1)};
}
export function mediaRange(doc, text, reference) {
    const start=Y.createAbsolutePositionFromRelativePosition(reference.start,doc),end=Y.createAbsolutePositionFromRelativePosition(reference.end,doc);
    if(!start||!end||start.type!==text||end.type!==text||start.index>=end.index)return null;
    return text.toString().slice(start.index,end.index)===reference.raw?{from:start.index,to:end.index}:null;
}
