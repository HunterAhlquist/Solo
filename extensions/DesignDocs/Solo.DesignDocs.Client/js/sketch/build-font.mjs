import { readFile, writeFile } from 'node:fs/promises';import { deflateSync } from 'node:zlib';
const glyphs=JSON.parse(await readFile('sketch/font-data.json','utf8'));
export async function buildFont(output){
 const width=96,height=48,raw=Buffer.alloc(height*(1+width*4));
 for(let code=32;code<128;code++){const c=String.fromCharCode(code),rows=glyphs[c]??glyphs[c.toUpperCase()]??glyphs['?'],gx=(code-32)%16*6,gy=Math.floor((code-32)/16)*8;
  for(let y=0;y<7;y++)for(let x=0;x<5;x++)if(rows[y]&(1<<(4-x))){const at=(gy+y)*(1+width*4)+1+(gx+x)*4;raw.fill(255,at,at+4);}}
 const crc=data=>{let value=0xffffffff;for(const byte of data){value^=byte;for(let n=0;n<8;n++)value=(value>>>1)^((value&1)?0xedb88320:0);}return (value^0xffffffff)>>>0;};
 const chunk=(name,data)=>{const type=Buffer.from(name),length=Buffer.alloc(4),checksum=Buffer.alloc(4);length.writeUInt32BE(data.length);checksum.writeUInt32BE(crc(Buffer.concat([type,data])));return Buffer.concat([length,type,data,checksum]);};
 const header=Buffer.alloc(13);header.writeUInt32BE(width);header.writeUInt32BE(height,4);header[8]=8;header[9]=6;
 await writeFile(`${output}/pixel-font.png`,Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]),chunk('IHDR',header),chunk('IDAT',deflateSync(raw)),chunk('IEND',Buffer.alloc(0))]));
}
