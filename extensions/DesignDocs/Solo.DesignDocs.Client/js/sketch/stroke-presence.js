// Collaborators watch a held stroke through presence. Each message carries the samples after
// those an earlier message already delivered, so the preview has every sample of the saved
// stroke, while each message stays small and under the relay's presence size limit.
export const presenceLimit=512;
const round=(value,scale)=>Number.isFinite(value)?Math.round(value*scale)/scale:0;
export function strokePresence(stroke,delivered=0){
    if(!stroke||!['pen','eraser'].includes(stroke.tool))return null;
    const {points,...rest}=stroke,base=Math.max(0,Math.min(delivered,points.length),points.length-presenceLimit);
    // Rendering is whole pixels; hundredths keep the shape and halve the message.
    return {...rest,base,points:points.slice(base).map(p=>[round(p[0],100),round(p[1],100),round(p[2],1000)])};
}
// Rebuilds each collaborator's held stroke from those messages. Sample indices are the
// sender's; `missing` counts samples a late join or a dropped message never delivered.
export class StrokeFeed {
    held=new Map();
    apply(client,stroke){
        if(!stroke||!Array.isArray(stroke.points)){this.held.delete(client);return;}
        // A message without a base is a whole stroke from an older client.
        const base=Number.isInteger(stroke.base)&&stroke.base>=0?stroke.base:0;
        let held=this.held.get(client);
        if(held?.id!==stroke.id||!Number.isInteger(stroke.base)||base-held.missing<0||base===0){held={missing:base,points:[]};this.held.set(client,held);}
        const {points,...rest}=stroke;Object.assign(held,rest);
        let at=base-held.missing;
        // A gap joins straight across what was never received.
        if(at>held.points.length){held.missing+=at-held.points.length;at=held.points.length;}
        held.points.length=at;for(const point of points)held.points.push(point);
        return held;
    }
    delete(client){this.held.delete(client);}
    values(){return this.held.values();}
}
