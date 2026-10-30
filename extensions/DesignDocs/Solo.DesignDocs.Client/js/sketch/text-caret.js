// Caret, selection and hit testing for the sketch's text editor, read from the
// same layout the canvas draws from.
//
// The editor is a transparent <textarea> laid over the canvas, so until now its
// own caret and selection highlight were the only positional cues — and a
// textarea has one font for all of its text. The moment a run differed in size
// the highlight kept the geometry of unformatted text while the canvas drew
// something taller, which is exactly the drift this file removes: positions
// come from `textLayout`, so they cannot disagree with the glyphs.
//
// Everything here is in the box's own coordinates (origin at box.x/box.y) and
// takes a canvas context purely to measure, never to draw.
import {runFont} from './font.js';

// Width of the first `count` characters of a run.
function advance(context,run,family,count){
    if(count<=0)return 0;
    context.font=runFont(run,family);
    return context.measureText(run.text.slice(0,count)).width;
}

// The line an index sits on. A newline belongs to the line it ends, so an index
// at a line's end is on that line rather than at the start of the next.
export function lineAt(layout,index){
    for(const line of layout.lines)if(index<=line.end)return line;
    return layout.lines.at(-1);
}

/**
 * The caret at `index`: a vertical bar sized by the run it would type into —
 * the one before it, as every editor does, falling back to the one after at the
 * start of a line.
 */
export function caretAt(context,layout,family,index){
    const line=lineAt(layout,index);
    if(!line)return {x:0,y:0,height:layout.lineHeight};
    const local=Math.max(0,Math.min(index-line.start,line.end-line.start));

    let x=0,before=null,after=null,seen=0;
    for(const run of line.runs){
        if(local<seen+run.text.length){
            x=run.x+advance(context,run,family,local-seen);
            after=run;
            if(local>seen)before=run;
            break;
        }
        seen+=run.text.length;x=run.x+run.width;before=run;after=null;
    }

    const run=before??after;
    const size=run?run.size:line.size;
    // A run is placed by its glyph top; the caret is its line box, which stands
    // a quarter of a size proud of the glyphs at each end.
    const top=run?line.top+run.top:line.top+(line.height-size)/2;
    return {x,y:top-size*.25,height:size*1.5};
}

/**
 * The highlight for [start, end), one rectangle per line it covers. Each is the
 * full height of its line, which is what a browser draws and what makes the
 * band grow with the text rather than with the editor's own font.
 */
export function selectionRects(context,layout,family,start,end){
    const from=Math.min(start,end),to=Math.max(start,end);
    if(to<=from)return [];

    const rects=[];
    for(const line of layout.lines){
        const a=Math.max(from,line.start),b=Math.min(to,line.end);
        if(b<a)continue;
        // The newline itself is in the range: the break is shown as a stub past
        // the last glyph, or the whole band on a line with nothing on it.
        const break_=to>line.end&&from<=line.end;
        const left=caretAt(context,layout,family,a).x,right=caretAt(context,layout,family,b).x;
        const width=Math.max(0,right-left)+(break_?line.size*.35:0);
        if(width<=0)continue;
        rects.push({x:left,y:line.top,width,height:line.height});
    }
    return rects;
}

/**
 * The character index nearest a point — what a click means. The textarea does
 * this itself from its own single font, so with mixed runs its answer and the
 * glyph under the pointer are different characters.
 */
export function offsetAt(context,layout,family,x,y){
    const line=layout.lines.find(candidate=>y<candidate.top+candidate.height)??layout.lines.at(-1);
    if(!line)return 0;
    if(x<=0)return line.start;

    for(const run of line.runs){
        if(x>run.x+run.width)continue;
        // Nearest boundary rather than the one before: clicking the right half
        // of a character puts the caret after it.
        context.font=runFont(run,family);
        let best=0,distance=Math.abs(x-run.x);
        for(let count=1;count<=run.text.length;count++){
            const to=Math.abs(x-(run.x+context.measureText(run.text.slice(0,count)).width));
            if(to<distance){distance=to;best=count;}
        }
        return run.index+best;
    }
    return line.end;
}

/**
 * The end a shift-click leaves alone. Which one is fixed depends on how the
 * selection was made rather than on where the click landed: a range dragged
 * right to left is anchored at its end, so shift-clicking inside it has to move
 * the start. Guessing from the click instead moves whichever end is nearer,
 * which silently reformats the wrong half of a backward selection.
 */
export function shiftAnchor({selectionStart,selectionEnd,selectionDirection}){
    return selectionDirection==='backward'?selectionEnd:selectionStart;
}

/** The word around `index`, for a double click. */
export function wordAt(text,index){
    let from=Math.max(0,Math.min(index,text.length)),to=from;
    while(from>0&&/\S/.test(text[from-1]))from--;
    while(to<text.length&&/\S/.test(text[to]))to++;
    // A click in whitespace selects nothing rather than the next word along.
    return [from,to];
}
