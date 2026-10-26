using System.Net;
using System.Text;
using Bunit;
using Solo.Client.Offline;
using Solo.Client.Auth;
using Microsoft.JSInterop;

namespace Solo.Client.Tests;

public sealed class OfflineCacheTests:ComponentTestBase
{
    [Theory][InlineData(401)][InlineData(403)]
    public async Task Explicit_access_denial_never_uses_cached_private_content(int code)
    {
        JSInterop.Setup<OfflineCache.CachedResponse?>("solo.offline.get",_=>true).SetResult(new(Convert.ToBase64String(Encoding.UTF8.GetBytes("private")),"application/json"));
        JSInterop.Setup<bool>("solo.offline.isOffline").SetResult(false);
        var expiry=new SessionExpiry();using var http=new HttpClient(new OfflineCacheHandler(new(JSInterop.JSRuntime),expiry){InnerHandler=new Handler(_=>new((HttpStatusCode)code))}){BaseAddress=new("https://solo.test")};
        var response=await http.GetAsync("api/projects");Assert.Equal((HttpStatusCode)code,response.StatusCode);Assert.Contains(JSInterop.Invocations,i=>i.Identifier=="solo.offline.lock");
    }
    [Fact]
    public async Task Transport_failure_uses_the_verified_accounts_cached_response()
    {
        JSInterop.Setup<OfflineCache.CachedResponse?>("solo.offline.get",_=>true).SetResult(new(Convert.ToBase64String(Encoding.UTF8.GetBytes("cached")),"text/plain"));
        JSInterop.Setup<bool>("solo.offline.isOffline").SetResult(false);
        using var http=new HttpClient(new OfflineCacheHandler(new(JSInterop.JSRuntime)){InnerHandler=new Handler(_=>throw new HttpRequestException())}){BaseAddress=new("https://solo.test")};
        Assert.Equal("cached",await http.GetStringAsync("api/projects"));Assert.Contains(JSInterop.Invocations,i=>i.Identifier=="solo.offline.network"&&Equals(i.Arguments[0],false));
    }
    [Theory][InlineData("api/users/me",true)][InlineData("api/extensions",true)][InlineData("api/extensions/design-docs/client/Solo.DesignDocs.Client.dll",true)]
    [InlineData("api/auth/sessions",false)][InlineData("api/settings",false)][InlineData("api/projects/secret/tasks",false)]
    public void Only_bootstrap_and_navigation_reads_enter_the_host_cache(string path,bool expected)=>Assert.Equal(expected,OfflineCache.Supports(path));
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(respond(r));
    }
}
