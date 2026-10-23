using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// Client-sent topic messages (contract 1.10), through the fixture extension's
/// handler: the only one in core's tests, so the only way to test the routing
/// end to end.
/// </summary>
[Collection("postgres")]
public sealed class LiveMessageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Topic = "fixture:doc";

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _admin = null!;
    private ProjectDto _project = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        // A small live allowance, so a burst can be exceeded in a test.
        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("live"), [SoloApiFactory.FixtureDirectory])
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("RateLimiting:Live:Burst", "20");
                builder.UseSetting("RateLimiting:Live:TokensPerSecond", "1");
            });
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
        _project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Live", alias = "LIVEMSG" });
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private HubConnection Connect(string sub, string email, string name) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, BoardHubConstants.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[TestAuthHandler.SubHeader] = sub;
                options.Headers[TestAuthHandler.EmailHeader] = email;
                options.Headers[TestAuthHandler.NameHeader] = name;
            })
            .Build();

    private async Task<HubConnection> SubscribedAsync(string sub, string email, string name, string topic = Topic)
    {
        var connection = Connect(sub, email, name);
        await connection.StartAsync();
        await connection.InvokeAsync(nameof(IBoardHubServer.SubscribeTopic), _project.Id, topic);
        return connection;
    }

    private static Task<LiveSendResult> SendAsync(
        HubConnection connection, Guid projectId, string topic, string kind, string? data = null) =>
        connection.InvokeAsync<LiveSendResult>(nameof(IBoardHubServer.SendTopicMessage), projectId, topic, kind, data);

    private static TaskCompletionSource<TopicMessage> Expect(HubConnection connection, string kind)
    {
        var heard = new TaskCompletionSource<TopicMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<TopicMessage>(nameof(IBoardClient.TopicMessage), m =>
        {
            if (m.Kind == kind)
            {
                heard.TrySetResult(m);
            }
        });
        return heard;
    }

    [SkippableFact]
    public async Task A_message_on_a_topic_not_held_is_refused()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var connection = Connect("admin", "admin@example.com", "Admin");
        await connection.StartAsync();

        Assert.Equal(LiveSendResult.NotSubscribed, await SendAsync(connection, _project.Id, Topic, "echo"));
        // A user topic has no project to answer for, so nothing is held under one.
        await connection.InvokeAsync(nameof(IBoardHubServer.SubscribeUserTopic), Topic);
        Assert.Equal(LiveSendResult.NotSubscribed, await SendAsync(connection, Guid.Empty, Topic, "echo"));
    }

    [SkippableTheory]
    [InlineData("roadmap:list")]
    [InlineData("fixtures:doc")]
    [InlineData("no-prefix")]
    public async Task Only_the_extension_named_by_the_prefix_hears_its_topic(string topic)
    {
        postgres.SkipUnlessDockerAvailable();
        await using var connection = await SubscribedAsync("admin", "admin@example.com", "Admin", topic);

        Assert.Equal(LiveSendResult.NoHandler, await SendAsync(connection, _project.Id, topic, "echo"));
    }

    [SkippableFact]
    public async Task Oversized_messages_are_refused_before_any_handler()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var connection = await SubscribedAsync("admin", "admin@example.com", "Admin");

        Assert.Equal(LiveSendResult.TooLarge, await SendAsync(connection, _project.Id, Topic, "echo",
            new string('x', BoardHubConstants.ClientMessageMaxLength + 1)));
        Assert.Equal(LiveSendResult.TooLarge, await SendAsync(connection, _project.Id, Topic,
            new string('k', BoardHubConstants.ClientMessageKindMaxLength + 1)));
    }

    /// <summary>The largest allowed message fits through the hub's receive limit, envelope and all.</summary>
    [SkippableFact]
    public async Task The_largest_allowed_message_arrives()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var connection = await SubscribedAsync("admin", "admin@example.com", "Admin");
        var echo = Expect(connection, "echo");
        var data = new string('y', BoardHubConstants.ClientMessageMaxLength);

        Assert.Equal(LiveSendResult.Accepted, await SendAsync(connection, _project.Id, Topic, "echo", data));
        Assert.Equal(data, (await echo.Task.WaitAsync(TimeSpan.FromSeconds(10))).Data);
    }

    [SkippableFact]
    public async Task A_connection_past_its_burst_is_rate_limited()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var connection = await SubscribedAsync("admin", "admin@example.com", "Admin");

        var results = new List<LiveSendResult>();
        for (var i = 0; i < 25; i++)
        {
            results.Add(await SendAsync(connection, _project.Id, Topic, "noop"));
        }

        Assert.Equal(20, results.Count(r => r == LiveSendResult.Accepted));
        Assert.Contains(LiveSendResult.RateLimited, results);

        // Another connection has its own bucket.
        await using var other = await SubscribedAsync("admin", "admin@example.com", "Admin");
        Assert.Equal(LiveSendResult.Accepted, await SendAsync(other, _project.Id, Topic, "noop"));
    }

    [SkippableFact]
    public async Task A_relayed_message_reaches_the_others_and_not_the_sender()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var sender = await SubscribedAsync("admin", "admin@example.com", "Admin");
        await using var sameUserOtherTab = await SubscribedAsync("admin", "admin@example.com", "Admin");
        await using var elsewhere = await SubscribedAsync("admin", "admin@example.com", "Admin", "fixture:other");
        var echoedToSender = false;
        sender.On<TopicMessage>(nameof(IBoardClient.TopicMessage), m => echoedToSender |= m.Kind == "relayed");
        var strayMessage = false;
        elsewhere.On<TopicMessage>(nameof(IBoardClient.TopicMessage), _ => strayMessage = true);
        var relayed = Expect(sameUserOtherTab, "relayed");

        Assert.Equal(LiveSendResult.Accepted, await SendAsync(sender, _project.Id, Topic, "relay", "hello"));

        var message = await relayed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((_project.Id, Topic, "hello"), (message.ProjectId, message.Topic, message.Data));
        Assert.NotEqual(Guid.Empty, message.ActorUserId);
        await Task.Delay(300);
        Assert.False(echoedToSender, "the sender heard its own relay");
        Assert.False(strayMessage, "a subscriber to another topic heard it");
    }

    [SkippableFact]
    public async Task A_handler_that_throws_or_stalls_reports_failure_and_the_hub_carries_on()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var connection = await SubscribedAsync("admin", "admin@example.com", "Admin");
        var echo = Expect(connection, "echo");

        Assert.Equal(LiveSendResult.Failed, await SendAsync(connection, _project.Id, Topic, "throw"));
        Assert.Equal(LiveSendResult.Failed, await SendAsync(connection, _project.Id, Topic, "slow"));
        Assert.Equal(LiveSendResult.Accepted, await SendAsync(connection, _project.Id, Topic, "echo", "still here"));
        Assert.Equal("still here", (await echo.Task.WaitAsync(TimeSpan.FromSeconds(10))).Data);
    }

    /// <summary>
    /// A handler decides who may change things from the sender's role, which
    /// the topic remembers. A demotion has to reach it without a reconnect, or
    /// a demoted member keeps sending edits until their tab happens to drop.
    /// </summary>
    [SkippableFact]
    public async Task The_sender_is_labelled_with_their_current_role()
    {
        postgres.SkipUnlessDockerAvailable();
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members",
            new { email = "reader@example.com", role = "Read" });
        var writer = await _admin.PostJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members",
            new { email = "writer@example.com", role = "ReadWrite" });

        await using var reader = await SubscribedAsync("reader", "reader@example.com", "Reader");
        await using var writing = await SubscribedAsync("writer", "writer@example.com", "Writer");
        await using var owner = await SubscribedAsync("admin", "admin@example.com", "Admin");

        Assert.Equal("Read|False|Reader", await WhoAmIAsync(reader));
        Assert.Equal("ReadWrite|True|Writer", await WhoAmIAsync(writing));
        Assert.EndsWith("|True|Admin", await WhoAmIAsync(owner));

        var demoted = await _admin.PutJson<ProjectMemberDto>(
            $"/api/projects/{_project.Id}/members/{writer.Id}", new { role = "Read", isOwner = false });
        Assert.Equal(Solo.Shared.ProjectRole.Read, demoted.Role);

        Assert.Equal("Read|False|Writer", await WhoAmIAsync(writing));
    }

    private async Task<string?> WhoAmIAsync(HubConnection connection)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = connection.On<TopicMessage>(nameof(IBoardClient.TopicMessage), m =>
        {
            if (m.Kind == "whoami")
            {
                answer.TrySetResult(m.Data);
            }
        });
        Assert.Equal(LiveSendResult.Accepted, await SendAsync(connection, _project.Id, Topic, "whoami"));
        return await answer.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
