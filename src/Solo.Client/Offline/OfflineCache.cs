using Microsoft.JSInterop;

namespace Solo.Client.Offline;

public sealed class OfflineCache(IJSRuntime js)
{
    public static bool Supports(string path)=>path is "api/config/auth" or "api/themes/active" or "api/users/me" or "api/projects" or "api/extensions"
        || System.Text.RegularExpressions.Regex.IsMatch(path,@"^api/projects/[0-9a-fA-F-]{36}$|^api/extensions/[a-z0-9-]+/client/[A-Za-z0-9_.-]+\.dll$");
    public async Task<CachedResponse?> Read(string path){try{return await js.InvokeAsync<CachedResponse?>("solo.offline.get",path);}catch(JSException){return null;}catch(InvalidOperationException){return null;}}
    public async Task Write(string path,CachedResponse value,long epoch){try{await js.InvokeVoidAsync("solo.offline.put",path,value,epoch);}catch(JSException){}catch(InvalidOperationException){}}
    public async Task<bool> IsOffline(){try{return await js.InvokeAsync<bool>("solo.offline.isOffline");}catch(JSException){return false;}}
    public async Task Network(bool connected){try{await js.InvokeVoidAsync("solo.offline.network",connected);}catch(JSException){}}
    public async Task<long> Epoch(){try{return await js.InvokeAsync<long>("solo.offline.epoch");}catch(JSException){return 0;}}
    public async Task Lock(long epoch){try{await js.InvokeVoidAsync("solo.offline.lock",epoch);}catch(JSException){}}
    public sealed record CachedResponse(string Data,string? ContentType);
}
