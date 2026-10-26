using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.JSInterop;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Client;

public sealed class OfflineApi(ISoloClientApi host,IJSRuntime js):ISoloClientApi
{
    private static readonly ConditionalWeakTable<ISoloClientApi,OfflineApi> Clients=new();
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static ISoloClientApi For(ISoloClientApi host,IJSRuntime js)=>Clients.GetValue(host,api=>new(api,js));
    private async Task<Response> Local(HttpMethod method,string path,object? body=null,byte[]? bytes=null,string? type=null,CancellationToken ct=default)
    {
        var module=await DesignDocsAssets.ModuleAsync(js,new(host));
        try{return await module.InvokeAsync<Response>("offlineRequest",ct,method.Method,path,body is null?null:JsonSerializer.Serialize(body,Json),bytes,type)??new();}
        catch(JSException e){throw new SoloApiException(System.Net.HttpStatusCode.Conflict,"Offline vault",e.Message);}
    }
    public Uri Resolve(string path)=>host.Resolve(path);
    public Uri ResolveExternal(string path)=>host.ResolveExternal(path);
    public async Task<T> GetAsync<T>(string path,CancellationToken ct=default){var r=await Local(HttpMethod.Get,path,ct:ct);return r.Handled?r.Value.Deserialize<T>(Json)!:await host.GetAsync<T>(path,ct);}
    public async Task<T> SendAsync<T>(HttpMethod method,string path,object? body=null,CancellationToken ct=default){var r=await Local(method,path,body,ct:ct);return r.Handled?r.Value.Deserialize<T>(Json)!:await host.SendAsync<T>(method,path,body,ct);}
    public async Task SendAsync(HttpMethod method,string path,object? body=null,CancellationToken ct=default){if(!(await Local(method,path,body,ct:ct)).Handled)await host.SendAsync(method,path,body,ct);}
    public async Task<SoloBinary> GetBytesAsync(string path,CancellationToken ct=default){var r=await Local(HttpMethod.Get,path,ct:ct);return r.Handled?new(r.Bytes??[],r.ContentType):await host.GetBytesAsync(path,ct);}
    public async Task<T> SendBytesAsync<T>(HttpMethod method,string path,byte[] content,string contentType,CancellationToken ct=default){var r=await Local(method,path,bytes:content,type:contentType,ct:ct);return r.Handled?r.Value.Deserialize<T>(Json)!:await host.SendBytesAsync<T>(method,path,content,contentType,ct);}
    private sealed class Response{public bool Handled{get;set;}public JsonElement Value{get;set;}public byte[]? Bytes{get;set;}public string? ContentType{get;set;}}
}
