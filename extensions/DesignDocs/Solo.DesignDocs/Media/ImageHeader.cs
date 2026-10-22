using System.Buffers.Binary;
namespace Solo.DesignDocs.Media;

// Header sniffing only, never a native decoder. Browser uploads are re-encoded
// before reaching this boundary; responses are isolated even for malformed data.
public static class ImageHeader
{
    public sealed record Info(string ContentType,int Width,int Height);
    public static Info? Read(ReadOnlySpan<byte> b)
    {
        if(b.Length>=24&&b[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})&&b.Slice(12,4).SequenceEqual("IHDR"u8))
            return Valid("image/png",BinaryPrimitives.ReadInt32BigEndian(b[16..]),BinaryPrimitives.ReadInt32BigEndian(b[20..]));
        if(b.Length>=30&&b[..4].SequenceEqual("RIFF"u8)&&b.Slice(8,4).SequenceEqual("WEBP"u8)) {
            if(b.Slice(12,4).SequenceEqual("VP8X"u8))return Valid("image/webp",1+U24(b[24..]),1+U24(b[27..]));
            if(b.Slice(12,4).SequenceEqual("VP8 "u8)&&b.Slice(23,3).SequenceEqual(new byte[]{157,1,42}))
                return Valid("image/webp",BinaryPrimitives.ReadUInt16LittleEndian(b[26..])&16383,BinaryPrimitives.ReadUInt16LittleEndian(b[28..])&16383);
            if(b.Slice(12,4).SequenceEqual("VP8L"u8)&&b[20]==47) {
                var bits=BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);return Valid("image/webp",1+(int)(bits&16383),1+(int)((bits>>14)&16383));
            }
        }
        if(b.Length>4&&b[0]==255&&b[1]==216) {
            var i=2;while(i+4<=b.Length) {
                if(b[i++]!=255)return null;while(i<b.Length&&b[i]==255)i++;if(i>=b.Length)return null;
                var marker=b[i++];if(marker is 0xd9 or 0xda)return null;if(marker is >=0xd0 and <=0xd7 or 0x01)continue;
                if(i+2>b.Length)return null;var length=BinaryPrimitives.ReadUInt16BigEndian(b[i..]);if(length<2||i+length>b.Length)return null;
                if(marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                    return length<8?null:Valid("image/jpeg",BinaryPrimitives.ReadUInt16BigEndian(b[(i+5)..]),BinaryPrimitives.ReadUInt16BigEndian(b[(i+3)..]));
                i+=length;
            }
        }
        return null;
    }
    private static int U24(ReadOnlySpan<byte>b)=>b[0]|b[1]<<8|b[2]<<16;
    private static Info? Valid(string type,int w,int h)=>w>0&&h>0&&w<=2560&&h<=2560?new(type,w,h):null;
}
