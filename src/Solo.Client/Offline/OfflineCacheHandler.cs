using System.Net;
using Solo.Client.Auth;

namespace Solo.Client.Offline;

public sealed class OfflineCacheHandler(OfflineCache cache,SessionExpiry? expiry=null):DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var epoch=await cache.Epoch();
        var path=request.RequestUri!.AbsolutePath.TrimStart('/');var eligible=request.Method==HttpMethod.Get&&string.IsNullOrEmpty(request.RequestUri.Query)&&OfflineCache.Supports(path);
        var cached=eligible?await cache.Read(path):null;
        if(epoch!=await cache.Epoch())cached=null;
        HttpResponseMessage Stored(){var r=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Convert.FromBase64String(cached!.Data))};if(cached.ContentType is {} type)r.Content.Headers.ContentType=new(type);return r;}
        if(cached is not null&&await cache.IsOffline())return Stored();
        using var attempt=CancellationTokenSource.CreateLinkedTokenSource(ct);
        if(cached is not null)attempt.CancelAfter(TimeSpan.FromSeconds(5));
        HttpResponseMessage response;
        try{response=await base.SendAsync(request,attempt.Token);}
        catch(Exception e)when(e is HttpRequestException||e is TaskCanceledException&&!ct.IsCancellationRequested){await cache.Network(false);if(cached is not null&&epoch==await cache.Epoch())return Stored();return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable){Content=System.Net.Http.Json.JsonContent.Create(new{title="Server unavailable",detail="Downloaded vaults remain available offline. Reconnect before using online features."})};}
        if(response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout){await cache.Network(false);if(cached is not null&&epoch==await cache.Epoch()){response.Dispose();return Stored();}return response;}
        await cache.Network(true);
        if(response.StatusCode==HttpStatusCode.Unauthorized){await cache.Lock(epoch);expiry?.NotifyExpired();return response;}
        if(eligible&&response.IsSuccessStatusCode)await cache.Write(path,new(Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync(ct)),response.Content.Headers.ContentType?.MediaType),epoch);
        return response;
    }
}
