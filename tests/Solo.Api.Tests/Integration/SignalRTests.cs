using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;
using Xunit;

namespace Solo.Api.Tests.Integration;

[Collection("postgres")]
public sealed class SignalRTests(PostgresFixture postgres) : IAsyncLifetime
{
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("signalr"));
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private HubConnection Connect(string sub, string email, string name)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, BoardHubConstants.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[TestAuthHandler.SubHeader] = sub;
                options.Headers[TestAuthHandler.EmailHeader] = email;
                options.Headers[TestAuthHandler.NameHeader] = name;
            })
            .Build();
    }

    [SkippableFact]
    public async Task SaveBroadcasts_ReachOtherBoardMembers_ButNotTheSender()
    {
        postgres.SkipUnlessDockerAvailable();

        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Live" });
        var board = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Live board" });

        await using var sender = Connect("admin", "admin@example.com", "Admin");
        await using var receiver = Connect("admin", "admin@example.com", "Admin");

        var received = new TaskCompletionSource<EntityChangedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.On<EntityChangedEvent>(nameof(IBoardClient.EntityChanged), e => received.TrySetResult(e));

        var senderGotEcho = false;
        sender.On<EntityChangedEvent>(nameof(IBoardClient.EntityChanged), _ => senderGotEcho = true);

        await sender.StartAsync();
        await receiver.StartAsync();
        await sender.InvokeAsync(nameof(IBoardHubServer.JoinBoard), board.Id);
        await receiver.InvokeAsync(nameof(IBoardHubServer.JoinBoard), board.Id);

        // REST save carrying the sender's connection id: broadcast excludes it.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/boards/{board.Id}/tasks")
        {
            Content = JsonContent.Create(new { name = "Broadcast me" }, options: TestClients.Json),
        };
        request.Headers.Add(BoardHubConstants.ConnectionIdHeader, sender.ConnectionId);
        var response = await _admin.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var change = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EntityTypes.Task, change.EntityType);
        Assert.Equal(ChangeKind.Created, change.Kind);
        Assert.NotEqual(Guid.Empty, change.ActorUserId);
        Assert.False(senderGotEcho, "the saving client should not receive its own change event");
    }

    [SkippableFact]
    public async Task Presence_IsBroadcastToTheBoardGroup()
    {
        postgres.SkipUnlessDockerAvailable();

        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Presence" });
        var board = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Presence board" });
        var epicId = (await _admin.PostJson<EpicDto>(
            $"/api/boards/{board.Id}/epics", new { name = "Watched" })).Id;

        await using var watcher = Connect("admin", "admin@example.com", "Admin");
        await using var editor = Connect("admin", "admin@example.com", "Admin");

        var sawEditing = new TaskCompletionSource<BoardPresenceSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.On<BoardPresenceSnapshot>(nameof(IBoardClient.PresenceChanged), snapshot =>
        {
            if (snapshot.Entries.Any(e => e.Mode == PresenceMode.Editing))
            {
                sawEditing.TrySetResult(snapshot);
            }
        });

        await watcher.StartAsync();
        await editor.StartAsync();
        await watcher.InvokeAsync(nameof(IBoardHubServer.JoinBoard), board.Id);
        await editor.InvokeAsync(nameof(IBoardHubServer.JoinBoard), board.Id);
        await editor.InvokeAsync(
            nameof(IBoardHubServer.SetPresence), EntityTypes.Epic, epicId, PresenceMode.Editing);

        var snapshot = await sawEditing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var entry = snapshot.Entries.Single(e => e.Mode == PresenceMode.Editing);
        Assert.Equal(EntityTypes.Epic, entry.EntityType);
        Assert.Equal(epicId, entry.EntityId);
        Assert.Equal("Admin", entry.DisplayName);
    }

    [SkippableFact]
    public async Task NonMembers_CannotJoinBoardGroups()
    {
        postgres.SkipUnlessDockerAvailable();

        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Locked" });
        var board = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Locked board" });

        // Provision the outsider, then try to join.
        var outsider = _factory.As("outsider", "outsider@example.com");
        await outsider.GetJson<UserDto>("/api/users/me");

        await using var connection = Connect("outsider", "outsider@example.com", "Outsider");
        await connection.StartAsync();
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => connection.InvokeAsync(nameof(IBoardHubServer.JoinBoard), board.Id));
    }
}
