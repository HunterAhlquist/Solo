// Bound presence packets without changing the samples in the saved stroke.
export function strokePresence(stroke) {
    if(!stroke||!['pen','eraser'].includes(stroke.tool))return null;
    const limit=128,source=stroke.points;
    const points=source.length<=limit?source.slice():Array.from({length:limit},(_,i)=>source[Math.round(i*(source.length-1)/(limit-1))]);
    return {...stroke,points};
}
