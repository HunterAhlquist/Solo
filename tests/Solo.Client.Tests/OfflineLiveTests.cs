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
            // The start fails, so the topic waits for the retry, but it was tried.
            await hub.SubscribeTopic(Guid.NewGuid(),"test:recovered");
            Assert.Contains("POST /api/auth/hub-ticket",stub.Paths);
            Assert.False(hub.IsConnected);
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
            // Returns at once, the topic held for when the connection comes back.
            await hub.SubscribeTopic(Guid.NewGuid(), "test:offline").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(hub.IsConnected);
        }
        finally { await hub.DisposeAsync(); await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task A_board_opened_while_the_connection_is_down_opens_and_joins_later()
    {
        // After the device was locked the connection is down for a while;
        // opening a board then threw, and the page showed its error banner.
        JSInterop.Setup<bool>("solo.offline.isOffline").SetResult(true);
        var hub = new BoardHubClient(new InstanceConnection("https://solo.test/"), null!, new ConnectionIdAccessor(), new OfflineCache(JSInterop.JSRuntime));
        try
        {
            var boardId = Guid.NewGuid();
            await hub.JoinBoard(boardId);
            await hub.SetPresence(Solo.Shared.Hubs.EntityTypes.Board, boardId, Solo.Shared.PresenceMode.Viewing);
            Assert.True(hub.IsActive);
            Assert.False(hub.IsConnected);
        }
        finally { await hub.DisposeAsync(); }
    }

    [Fact]
    public async Task A_board_left_before_the_connection_came_up_is_not_rejoined()
    {
        // The join waits for the reconnect; leaving first has to cancel it,
        // or the reconnect rejoins a board nobody is looking at.
        JSInterop.Setup<bool>("solo.offline.isOffline").SetResult(true);
        var hub = new BoardHubClient(new InstanceConnection("https://solo.test/"), null!, new ConnectionIdAccessor(), new OfflineCache(JSInterop.JSRuntime));
        try
        {
            var boardId = Guid.NewGuid();
            await hub.JoinBoard(boardId);
            Assert.True(hub.IsActive);

            await hub.LeaveBoard(boardId);
            Assert.False(hub.IsActive);
        }
        finally { await hub.DisposeAsync(); }
    }
}
