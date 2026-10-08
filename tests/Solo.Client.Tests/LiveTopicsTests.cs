using Microsoft.Extensions.Logging.Abstractions;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests;

/// <summary>How the host hands its live connection's topic traffic to extensions.</summary>
public class LiveTopicsTests
{
    private static readonly Guid Project = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Other = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly FakeBoardHub _hub = new();
    private readonly LiveTopics _topics;

    public LiveTopicsTests()
    {
        var http = new StubHttp();
        http.RouteJson("/api/users/me", new UserDto(TestData.MeId, "h@x.io", "Hunter", null, false));
        var session = new UserSession(http.CreateClient());
        session.LoadAsync().GetAwaiter().GetResult();
        _topics = new LiveTopics(_hub, session, NullLogger<LiveTopics>.Instance);
    }

    private static TopicPresenceSnapshot Snapshot(string topic, params (Guid User, PresenceMode Mode)[] entries) =>
        new(Project, topic, entries.Select(e => new TopicPresenceEntry(e.User, "Someone", e.Mode)).ToList());

    [Fact]
    public async Task A_message_reaches_the_subscribers_of_its_topic_and_project_only()
    {
        var heard = new List<string>();
        await _topics.SubscribeAsync(Project, "plan:a", m => { heard.Add($"a:{m.Kind}"); return Task.CompletedTask; });
        await _topics.SubscribeAsync(Project, "plan:b", m => { heard.Add($"b:{m.Kind}"); return Task.CompletedTask; });

        await _hub.RaiseTopicMessage(new TopicMessage(Project, "plan:a", "changed", null, Guid.Empty));
        await _hub.RaiseTopicMessage(new TopicMessage(Other, "plan:a", "elsewhere", null, Guid.Empty));

        Assert.Equal(["a:changed"], heard);
        Assert.Equal([(Project, "plan:a"), (Project, "plan:b")], _hub.Subscribed);
    }

    [Fact]
    public async Task A_user_topic_hears_its_own_messages_and_lets_go_on_dispose()
    {
        var heard = new List<string>();
        var mine = await _topics.SubscribeUserAsync("inbox", m => { heard.Add($"user:{m.Kind}"); return Task.CompletedTask; });
        await _topics.SubscribeAsync(Project, "inbox", m => { heard.Add($"project:{m.Kind}"); return Task.CompletedTask; });

        // A user topic's messages carry no project, so a project topic of the
        // same name doesn't hear them, nor they it.
        await _hub.RaiseTopicMessage(new TopicMessage(Guid.Empty, "inbox", "unread", "3", Guid.Empty));
        await _hub.RaiseTopicMessage(new TopicMessage(Project, "inbox", "changed", null, Guid.Empty));

        Assert.Equal(["user:unread", "project:changed"], heard);
        Assert.Equal(["inbox"], _hub.UserSubscribed);

        await mine.DisposeAsync();
        Assert.Equal(["inbox"], _hub.UserUnsubscribed);
        Assert.Empty(_hub.Unsubscribed);
    }

    [Fact]
    public async Task Presence_leaves_out_the_signed_in_user()
    {
        IReadOnlyList<TopicPresenceEntry>? seen = null;
        await _topics.SubscribeAsync(Project, "plan:a", _ => Task.CompletedTask,
            entries => { seen = entries; return Task.CompletedTask; });

        await _hub.RaiseTopicPresence(Snapshot("plan:a",
            (TestData.MeId, PresenceMode.Viewing), (Other, PresenceMode.Editing)));

        var entry = Assert.Single(seen!);
        Assert.Equal((Other, PresenceMode.Editing), (entry.UserId, entry.Mode));
    }

    [Fact]
    public async Task A_late_subscriber_hears_who_is_already_there()
    {
        await _topics.SubscribeAsync(Project, "plan:a", _ => Task.CompletedTask, _ => Task.CompletedTask);
        await _hub.RaiseTopicPresence(Snapshot("plan:a", (Other, PresenceMode.Viewing)));

        IReadOnlyList<TopicPresenceEntry>? seen = null;
        await _topics.SubscribeAsync(Project, "plan:a", _ => Task.CompletedTask,
            entries => { seen = entries; return Task.CompletedTask; });

        Assert.Equal(Other, Assert.Single(seen!).UserId);
    }

    [Fact]
    public async Task Letting_go_clears_presence_and_the_last_holder_unsubscribes()
    {
        var page = await _topics.SubscribeAsync(Project, "plan:a", _ => Task.CompletedTask);
        var list = await _topics.SubscribeAsync(Project, "plan:a", _ => Task.CompletedTask);
        await _topics.SetPresenceAsync(Project, "plan:a", PresenceMode.Viewing);

        await page.DisposeAsync();
        // Someone still listens, so only the presence goes.
        Assert.Equal(PresenceMode.None, _hub.TopicPresenceReports[^1].Mode);

        await list.DisposeAsync();
        Assert.Equal(2, _hub.Unsubscribed.Count);
    }

    [Fact]
    public async Task One_handler_failing_does_not_stop_the_next()
    {
        var reached = false;
        await _topics.SubscribeAsync(Project, "plan:a", _ => throw new InvalidOperationException("broken"));
        await _topics.SubscribeAsync(Project, "plan:a", _ => { reached = true; return Task.CompletedTask; });

        await _hub.RaiseTopicMessage(new TopicMessage(Project, "plan:a", "changed", null, Guid.Empty));

        Assert.True(reached);
    }
}
