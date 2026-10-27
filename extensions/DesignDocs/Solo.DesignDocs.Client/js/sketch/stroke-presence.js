// Collaborators watch a held stroke through presence. Each message carries samples from the
// first one no message has delivered yet, at most `presenceLimit` of them. The oldest go
// first, so someone joining mid-stroke (delivery resets to zero) is filled in from the start
// over a few messages, while each message stays under the relay's presence size limit.
export const presenceLimit=512;
const round=(value,scale)=>Number.isFinite(value)?Math.round(value*scale)/scale:0;
export function strokePresence(stroke,delivered=0){
    if(!stroke||!['pen','eraser'].includes(stroke.tool))return null;
    const {points,...rest}=stroke,base=Math.max(0,Math.min(delivered,points.length));
    // Rendering is whole pixels; hundredths keep the shape and halve the message.
    return {...rest,base,points:points.slice(base,base+presenceLimit).map(p=>[round(p[0],100),round(p[1],100),round(p[2],1000)])};
}
const same=(a,b)=>a===b||(!!a&&!!b&&a[0]===b[0]&&a[1]===b[1]&&a[2]===b[2]);
// Rebuilds each collaborator's held stroke from those messages, at the sender's sample
// indices. Samples not received yet are holes, filled when they arrive. `revision` changes
// whenever a sample before the end is filled or changed, so a renderer that extends what it
// already drew knows to start over.
export class StrokeFeed {
    held=new Map();
    apply(client,stroke){
        if(!stroke||!Array.isArray(stroke.points)){this.held.delete(client);return;}
        // A message without a base is a whole stroke from an older client.
        const whole=!Number.isInteger(stroke.base)||stroke.base<0,base=whole?0:stroke.base;
        let held=this.held.get(client);
        if(held?.id!==stroke.id||whole){held={points:[],revision:(held?.revision??0)+1};this.held.set(client,held);}
        const {points,base:_,...rest}=stroke;Object.assign(held,rest);
        points.forEach((point,index)=>{
            const at=base+index;
            if(at<held.points.length&&!same(held.points[at],point))held.revision++;
            held.points[at]=point;
        });
        return held;
    }
    delete(client){this.held.delete(client);}
    values(){return this.held.values();}
}
