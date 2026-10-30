// Text layout for the canvas. A box's string carries formatting as Y.Text
// attributes, so a box is a sequence of runs rather than one styled block:
// bold, italic, underline, size and ink each apply to a range the author
// selected. Attributes a run does not set fall back to the box.
//
// Runs are laid out bottom-aligned within their line rather than baseline-
// aligned: canvas gives no ascent without measuring every run, and for text in
// one family the two agree closely. The single-run case reduces exactly to the
// geometry this file had before runs existed, which is what keeps existing
// sketches rendering unchanged.
const MIN=8,MAX=64;
export function textSize(box){return Math.max(MIN,Math.min(MAX,box.fontSize??(box.scale??2)*8));}
export function runSize(run,base){return Math.max(MIN,Math.min(MAX,run.size??base));}
export function runFont(run,family){return `${run.italic?'italic ':''}${run.bold?'700 ':''}${run.size}px ${family}`;}

// A box's runs, from the Y.Text delta when it has one and from the plain string
// otherwise — an older sketch, and the export path, both hand over a string.
export function textRuns(box){
    const delta=Array.isArray(box.runs)&&box.runs.length?box.runs:[{insert:String(box.text??'')}];
    return delta.map(part=>({...(part.attributes??{}),text:String(part.insert??'')}));
}

export function textLayout(context,box,family){
    const base=textSize(box),rows=[{runs:[],start:0}];
    // Character offsets travel with the layout because the editor needs them:
    // its caret and selection are indices into the string, and the only thing
    // that can turn one into a position is the layout that drew the glyph.
    let offset=0;
    for(const run of textRuns(box)){
        const size=runSize(run,base);
        // A run spans newlines; each one starts a row, including a trailing one
        // so a final blank line still takes height.
        String(run.text).split('\n').forEach((piece,index)=>{
            if(index){offset+=1;rows.push({runs:[],start:offset});}
            if(piece)rows.at(-1).runs.push({...run,text:piece,size,index:offset});
            offset+=piece.length;
        });
    }

    let top=0;
    const lines=rows.map(row=>{
        const runs=row.runs,size=Math.max(base,...runs.map(run=>run.size)),height=size*1.5;
        let x=0;
        const placed=runs.map(run=>{
            context.font=runFont(run,family);
            const width=context.measureText(run.text).width;
            // (height-size)/2 is the box's own padding; the second term drops a
            // smaller run so its bottom sits on the line's.
            const value={...run,x,width,top:(height-size)/2+(size-run.size)};
            x+=width;return value;
        });
        const line={runs:placed,width:x,height,size,top,
            start:row.start,end:row.start+runs.reduce((n,run)=>n+run.text.length,0)};
        top+=height;return line;
    });

    return {size:base,lineHeight:base*1.5,lines,
        width:Math.max(1,...lines.map(line=>line.width)),height:top,
        // Kept for callers that only want the text back.
        get text(){return rows.map(row=>row.runs.map(run=>run.text).join('')).join('\n');}};
}

// The one face that fits every run, or null when they differ. The overlay
// textarea can only have one, and matching it where there is a right answer is
// what keeps its own hit testing — which decides where a click puts the caret —
// agreed with the glyphs the canvas drew.
export function uniformFace(layout){
    let face=null;
    for(const line of layout.lines)for(const run of line.runs){
        const current={size:run.size,bold:!!run.bold,italic:!!run.italic};
        if(!face)face=current;
        else if(face.size!==current.size||face.bold!==current.bold||face.italic!==current.italic)return null;
    }
    return face;
}

export async function loadFont(root=document.body){
    await document.fonts.ready;
    // `palette` maps an ink name to a colour. A run without one keeps whatever
    // fill the caller set from the box's ink, so an unformatted box is drawn
    // exactly as before.
    const font=(context,box,palette)=>{
        const family=getComputedStyle(root).fontFamily,layout=textLayout(context,box,family),fill=context.fillStyle;
        context.textBaseline='top';
        for(const line of layout.lines)for(const run of line.runs){
            context.font=runFont(run,family);
            context.fillStyle=(run.ink&&palette?.[run.ink])||fill;
            const x=box.x+run.x,y=box.y+line.top+run.top;
            context.fillText(run.text,x,y);
            if(run.underline){
                // Canvas has no text decoration, so the rule is drawn. Thickness
                // tracks the run so a 48px word is not underlined with a hairline.
                const thickness=Math.max(1,Math.round(run.size/14));
                context.fillRect(x,Math.round(y+run.size*1.08),run.width,thickness);
            }
        }
        context.fillStyle=fill;
    };
    font.bounds=(context,box)=>{
        const layout=textLayout(context,box,getComputedStyle(root).fontFamily);
        return {left:box.x,top:box.y,right:box.x+layout.width,bottom:box.y+layout.height};
    };
    return font;
}
