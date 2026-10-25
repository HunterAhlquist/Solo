export const chunkLength = 32000;
export function encode(bytes) {
    let value = ''; for (let i = 0; i < bytes.length; i += 8192) value += String.fromCharCode(...bytes.subarray(i, i + 8192));
    return btoa(value);
}
export const decode = value => Uint8Array.from(atob(value), c => c.charCodeAt(0));
export function parts(data) { const result = []; for (let i = 0; i < data.length; i += chunkLength) result.push(data.slice(i, i + chunkLength)); return result; }
export function sequence(a, b) { return BigInt(a) > BigInt(b) ? a : b; }
export function unpackUpdates(bytes){
    const view=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength);if(bytes.length<4)throw new Error('Incomplete update batch.');
    const count=view.getUint32(0,true),updates=[];let offset=4;
    if(count>Math.floor((bytes.length-4)/5))throw new Error('Invalid update batch.');
    for(let i=0;i<count;i++){
        if(offset+4>bytes.length)throw new Error('Incomplete update batch.');
        const length=view.getUint32(offset,true);offset+=4;
        if(!length||offset+length>bytes.length)throw new Error('Invalid update length.');
        updates.push(bytes.subarray(offset,offset+length));offset+=length;
    }
    if(offset!==bytes.length)throw new Error('Trailing update data.');return updates;
}
