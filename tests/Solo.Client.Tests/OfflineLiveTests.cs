using Bunit;
using Solo.Client.Api;
using Solo.Client.Offline;
using Solo.Client.Realtime;

namespace Solo.Client.Tests;

public sealed class OfflineLiveTests : ComponentTestBase
{
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
