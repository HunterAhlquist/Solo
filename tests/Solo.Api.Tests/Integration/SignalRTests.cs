using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
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

        var project = await _admin.PostJson<ProjectDto>(
            "/api/projects", new { name = "Live", alias = "LIVE" });
        var board = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Live board", alias = "LIVEB" });

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

        var project = await _admin.PostJson<ProjectDto>(
            "/api/projects", new { name = "Presence", alias = "PRESENCE" });
        var board = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Presence board", alias = "PRESB" });
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

        var project = await _admin.PostJson<ProjectDto>(
            "/api/projects", new { name = "Locked", alias = "LOCKED" });
        var board = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Locked board", alias = "LOCKEDB" });

        // An identity nobody invited doesn't get as far as joining: the hub is
        // on the default policy, which wants a provisioned user.
        await using var uninvited = Connect("drifter", "drifter@example.com", "Drifter");
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => uninvited.StartAsync());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // A real user who belongs to another project connects, and still can't
        // join this one's board.
        await MemberElsewhereAsync("outsider@example.com");
        await using var connection = Connect("outsider", "outsider@example.com", "Outsider");
        await connection.StartAsync();
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => connection.InvokeAsync(nameof(IBoardHubServer.JoinBoard), board.Id));
    }

    /// <summary>Makes <paramref name="email"/> a provisioned user who belongs to a different project.</summary>
    private async Task MemberElsewhereAsync(string email)
    {
        var elsewhere = await _admin.PostJson<ProjectDto>(
            "/api/projects", new { name = "Elsewhere", alias = "ELSE" });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{elsewhere.Id}/members", new { email, role = "Read" });
    }

    // -----------------------------------------------------------------------
    // Extension topics (contract 1.4)
    // -----------------------------------------------------------------------

    [SkippableFact]
    public async Task A_published_topic_message_reaches_the_topics_subscribers_only()
    {
        postgres.SkipUnlessDockerAvailable();
        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Topics", alias = "TOPICS" });

        await using var listener = Connect("admin", "admin@example.com", "Admin");
        await using var elsewhere = Connect("admin", "admin@example.com", "Admin");
        var received = new TaskCompletionSource<TopicMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.On<TopicMessage>(nameof(IBoardClient.TopicMessage), m => received.TrySetResult(m));
        var strayMessage = false;
        elsewhere.On<TopicMessage>(nameof(IBoardClient.TopicMessage), _ => strayMessage = true);
        await listener.StartAsync();
        await elsewhere.StartAsync();
        await listener.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), project.Id, "test:plan");
        await elsewhere.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), project.Id, "test:other");

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Solo.Extensibility.ISoloLiveUpdates>()
                .PublishAsync(project.Id, "test:plan", "changed", "42");
        }

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((project.Id, "test:plan", "changed", "42"), (message.ProjectId, message.Topic, message.Kind, message.Data));
        await Task.Delay(300);
        Assert.False(strayMessage, "a subscriber to another topic heard it");
    }

    [SkippableFact]
    public async Task Topic_presence_follows_a_reader_onto_and_off_the_topic()
    {
        postgres.SkipUnlessDockerAvailable();
        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Present", alias = "PRESENT" });
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{project.Id}/members",
            new { email = "reader@example.com", role = "Read" });
        _factory.As("reader", "reader@example.com", "Reader");

        await using var watcher = Connect("admin", "admin@example.com", "Admin");
        var snapshots = new List<TopicPresenceSnapshot>();
        var sawReader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawReaderLeave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.On<TopicPresenceSnapshot>(nameof(IBoardClient.TopicPresenceChanged), snapshot =>
        {
            snapshots.Add(snapshot);
            if (snapshot.Entries.Any(e => e.DisplayName == "Reader" && e.Mode == PresenceMode.Viewing))
            {
                sawReader.TrySetResult();
            }
            else if (sawReader.Task.IsCompleted && snapshot.Entries.All(e => e.DisplayName != "Reader"))
            {
                sawReaderLeave.TrySetResult();
            }
        });
        await watcher.StartAsync();
        await watcher.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), project.Id, "test:page");

        var reader = Connect("reader", "reader@example.com", "Reader");
        await reader.StartAsync();
        await reader.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), project.Id, "test:page");
        await reader.InvokeAsync(nameof(IBoardHubServer.SetTopicPresence), project.Id, "test:page", PresenceMode.Viewing);
        await sawReader.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Closing the tab is leaving: the snapshot drops them without an explicit "None".
        await reader.DisposeAsync();
        await sawReaderLeave.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [SkippableFact]
    public async Task Only_members_subscribe_and_only_subscribers_report_presence()
    {
        postgres.SkipUnlessDockerAvailable();
        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Guarded", alias = "GUARDED" });
        await MemberElsewhereAsync("stranger@example.com");

        await using var stranger = Connect("stranger", "stranger@example.com", "Stranger");
        await stranger.StartAsync();
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => stranger.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), project.Id, "test:plan"));

        await using var member = Connect("admin", "admin@example.com", "Admin");
        await member.StartAsync();
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => member.InvokeAsync(nameof(IBoardHubServer.SetTopicPresence), project.Id, "test:plan", PresenceMode.Viewing));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => member.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), project.Id, new string('x', 201)));
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => member.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), project.Id, " "));
    }
}
