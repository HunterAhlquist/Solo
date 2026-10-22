using Microsoft.Extensions.Caching.Memory;
namespace Solo.DesignDocs.Sync;

// Bounded, expiring buffers never retain a disconnected client's partial upload indefinitely.
public sealed class RelayState : IDisposable
{
    private readonly MemoryCache _cache=new(new MemoryCacheOptions {SizeLimit=4096});
    public sealed record Lease(string Connection,Guid Token);
    private sealed class Parts(int count) { public string?[] Values {get;}=new string?[count]; public int Length; }
    public Lease Seed(Guid doc,string connection)
    {
        lock(_cache) {
            if(_cache.TryGetValue<Lease>(("seed",doc),out var lease))return lease!;
            lease=new(connection,Guid.NewGuid());_cache.Set(("seed",doc),lease,new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromSeconds(30)));return lease;
        }
    }
    public bool HoldsSeed(Guid doc,string connection,Guid token) {lock(_cache)return _cache.TryGetValue<Lease>(("seed",doc),out var l)&&l!.Connection==connection&&l.Token==token;}
    public void Seeded(Guid doc) {lock(_cache)_cache.Remove(("seed",doc));}
    public string? Part(string connection,Guid doc,Guid id,int index,int count,string data)
    {
        if(count is <1 or >512||index<0||index>=count||data.Length>32000)throw new FormatException();
        lock(_cache) {
            var key=(connection,doc,id);
            if(!_cache.TryGetValue<Parts>(key,out var p)) {p=new(count);_cache.Set(key,p,new MemoryCacheEntryOptions().SetSize(count).SetAbsoluteExpiration(TimeSpan.FromSeconds(30)));}
            if(p!.Values.Length!=count)throw new FormatException();
            if(p.Values[index] is null){p.Values[index]=data;p.Length+=data.Length;}
            else if(p.Values[index]!=data)throw new FormatException();
            if(p.Length>16000000){_cache.Remove(key);throw new FormatException();}
            if(p.Values.Any(v=>v is null))return null;
            _cache.Remove(key);return string.Concat(p.Values);
        }
    }
    public void Dispose()=>_cache.Dispose();
}
