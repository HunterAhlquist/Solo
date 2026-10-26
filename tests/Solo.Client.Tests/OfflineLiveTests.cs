using Bunit;
using Solo.Client.Api;
using Solo.Client.Offline;
using Solo.Client.Realtime;

namespace Solo.Client.Tests;

public sealed class OfflineLiveTests : ComponentTestBase
{
    [Fact]
    public async Task A_recovered_server_is_contacted_even_if_the_cached_network_flag_is_offline()
    {
        JSInterop.Setup<bool>("solo.offline.isOffline").SetResult(true);
        JSInterop.Setup<bool>("solo.offline.reconnect").SetResult(true);
        var stub=new StubHttp().Respond(System.Net.HttpStatusCode.ServiceUnavailable);
        using var http=new HttpClient(stub){BaseAddress=new("https://solo.test/")};
        var instance=new InstanceConnection("https://solo.test/");
        var hub=new BoardHubClient(instance,new ApiClient(http,instance),new ConnectionIdAccessor(),new OfflineCache(JSInterop.JSRuntime));
        try
        {
            await Assert.ThrowsAsync<ApiException>(()=>hub.SubscribeTopic(Guid.NewGuid(),"test:recovered"));
            Assert.Contains("POST /api/auth/hub-ticket",stub.Paths);
        }
        finally{await hub.DisposeAsync();}
    }
    [Fact]
    public async Task An_offline_topic_does_not_wait_for_a_network_connection()
    {
        JSInterop.Setup<bool>("solo.offline.isOffline").SetResult(true);
        var hub = new BoardHubClient(new InstanceConnection("https://solo.test/"), null!, new ConnectionIdAccessor(), new OfflineCache(JSInterop.JSRuntime));
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => hub.SubscribeTopic(Guid.NewGuid(), "test:offline"));
            Assert.False(hub.IsConnected);
        }
        finally { await hub.DisposeAsync(); await hub.DisposeAsync(); }
    }
}
