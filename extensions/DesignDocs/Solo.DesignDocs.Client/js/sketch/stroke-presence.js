// Presence repeatedly sends the current preview, so its size must stay bounded
// even when Pencil coalesces thousands of samples into one held stroke. Keep
// the complete samples locally; only the disposable remote preview is sampled.
export function strokePresence(stroke) {
    if(!stroke||!['pen','eraser'].includes(stroke.tool))return null;
    const limit=128,source=stroke.points;
    const points=source.length<=limit?source.slice():Array.from({length:limit},(_,i)=>source[Math.round(i*(source.length-1)/(limit-1))]);
    return {...stroke,points};
}
