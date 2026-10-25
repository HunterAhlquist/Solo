using System.Text.Json;
namespace Solo.DesignDocs.Sync;

// Mirrored by js/sync/protocol.js. Sequences are strings across the WASM/JS boundary.
public static class DocProtocol
{
    public const int ChunkLength=32000;
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public sealed record Request(string? AfterSeq=null,Guid Id=default,string? Data=null,int Index=0,int Count=0,string? UptoSeq=null,Guid? SeedToken=null,bool Batch=false,Guid? SyncId=null);
    public static byte[] Pack(IReadOnlyList<byte[]> updates)
    {
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write(updates.Count);
        foreach(var update in updates){writer.Write(update.Length);writer.Write(update);}
        return stream.ToArray();
    }
    public static string Serialize(object value)=>JsonSerializer.Serialize(value,Json);
}
