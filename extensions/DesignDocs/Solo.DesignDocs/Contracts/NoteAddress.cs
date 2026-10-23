namespace Solo.DesignDocs.Contracts;

public static class NoteAddress
{
    public const string Prefix="/docs/note/";
    // Encode all 128 bits: shortening to 11 characters would require a separate ID mapping.
    public static string Encode(Guid id)=>Convert.ToBase64String(id.ToByteArray(bigEndian:true)).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static string Url(Guid id)=>Prefix+Encode(id);
    public static bool TryDecode(string? value,out Guid id)
    {
        id=default;
        if(value is null)return false;
        if(value.Length is 32 or 36&&Guid.TryParse(value,out id))return true;
        if(value.Length!=22||value.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_'))return false;
        Span<byte> bytes=stackalloc byte[16];
        if(!Convert.TryFromBase64String(value.Replace('-','+').Replace('_','/')+"==",bytes,out var written)||written!=16)return false;
        id=new Guid(bytes,bigEndian:true);
        return Encode(id)==value;
    }
    public static bool TryReadUrl(string url,out Guid id)
    {
        id=default;return url.StartsWith(Prefix,StringComparison.Ordinal)&&TryDecode(url[Prefix.Length..].Split('#',2)[0],out id);
    }
    public const string UrlPattern=@"/docs/note/(?:[0-9a-fA-F-]{32,36}|[A-Za-z0-9_-]{22})(?![A-Za-z0-9_-])(?:#[\p{L}\p{N}_%-]+)?";
}
