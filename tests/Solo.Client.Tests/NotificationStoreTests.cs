using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests;

/// <summary>
/// The store's device side: in the iPhone and iPad app it schedules the
/// deadline reminders still to come after every refresh, and a tapped
/// notification, scheduled or shown, opens what it's about.
/// </summary>
public class NotificationStoreTests : ComponentTestBase
{
    private static readonly Guid TaskId = Guid.NewGuid();
    private static readonly Guid OtherTaskId = Guid.NewGuid();

    private readonly StubHttp _http = new();
    private readonly FakeDrawer _drawer = new();
    private NotificationInboxDto _inbox;

    public NotificationStoreTests()
    {
        _inbox = new NotificationInboxDto(
        [
            Entry(Guid.NewGuid(), TaskId, read: false),
            Entry(Guid.NewGuid(), OtherTaskId, read: false),
        ], 2);

        // Most specific first: the stub matches by substring.
        _http.RouteJson("api/notifications/upcoming", new List<UpcomingReminderDto>
        {
            new($"DueThisWeek:{TaskId}:2027-03-31", NotificationKind.DueThisWeek, TaskId,
                new DateTimeOffset(2027, 3, 24, 16, 0, 0, TimeSpan.Zero), "GAME-SP-1 Ship demo is due in 1 week", "Game · Sprint"),
        });
        _http.RouteJson("api/notifications/preferences", new NotificationPreferencesDto(
            "America/Los_Angeles", true, true, true, true, DigestFrequency.Daily, 8, DayOfWeek.Monday, 1));
        _http.Route("api/notifications", System.Net.HttpStatusCode.OK, () => _inbox);
        JSInterop.Setup<bool>("solo.notifications.attended").SetResult(true);
        JSInterop.Setup<bool>("solo.notifications.attach", _ => true).SetResult(true);
        JSInterop.Setup<int>("solo.notifications.schedule", _ => true).SetResult(1);
    }

    private static NotificationDto Entry(Guid id, Guid taskId, bool read) => new(
        id, NotificationKind.DueThisWeek, "Due", null, Guid.NewGuid(), Guid.NewGuid(), taskId, null,
        DateTimeOffset.UtcNow, read ? DateTimeOffset.UtcNow : null);

    private NotificationStore Store(bool inApp)
    {
        JSInterop.Setup<bool>("solo.notifications.canSchedule").SetResult(inApp);
        return new NotificationStore(
            _http.CreateClient(), new FakeTopics(), _drawer, Services.GetRequiredService<NavigationManager>(),
            JSInterop.JSRuntime, Services.GetRequiredService<ISnackbar>(), TimeProvider.System,
            NullLogger<NotificationStore>.Instance);
    }

    private int Scheduled => JSInterop.Invocations["solo.notifications.schedule"].Count;

    [Fact]
    public async Task In_the_app_every_refresh_schedules_the_reminders_still_to_come()
    {
        var store = Store(inApp: true);
        await store.LoadAsync();

        Assert.Equal(1, Scheduled);
        var reminders = Assert.IsAssignableFrom<IEnumerable<UpcomingReminderDto>>(
            JSInterop.Invocations["solo.notifications.schedule"][0].Arguments[0]);
        Assert.Equal(TaskId, Assert.Single(reminders).TaskId);

        await store.RefreshAsync();
        await store.PageVisible();
        Assert.Equal(3, Scheduled);
        Assert.Single(JSInterop.Invocations["solo.notifications.attach"]);
    }

    [Fact]
    public async Task Listens_for_taps_before_its_first_call_to_the_app()
    {
        // The app hands a tap that launched it to the page on the page's first
        // call; scheduling first would deliver it to nothing.
        await Store(inApp: true).LoadAsync();

        var calls = JSInterop.Invocations.Select(i => i.Identifier).ToList();
        Assert.True(calls.IndexOf("solo.notifications.attach") < calls.IndexOf("solo.notifications.schedule"), string.Join(", ", calls));
    }

    [Fact]
    public async Task A_browser_schedules_nothing_and_never_asks_for_reminders()
    {
        var store = Store(inApp: false);
        await store.LoadAsync();
        await store.PageVisible();

        Assert.Empty(JSInterop.Invocations["solo.notifications.schedule"]);
        Assert.Empty(JSInterop.Invocations["solo.notifications.claim"]);
        Assert.DoesNotContain(_http.Paths, p => p.Contains("upcoming"));
    }

    [Fact]
    public async Task In_the_app_an_entry_the_app_already_announced_is_not_announced_again()
    {
        // The app reads the inbox while it's closed and announces what's new;
        // the page, back in view, announces only what the app answers it hadn't.
        static string Key(NotificationDto n) => $"{n.Id}|{n.CreatedAt.ToUnixTimeMilliseconds()}";
        static JsonElement[] Sent(JSRuntimeInvocation i) => JsonSerializer.SerializeToElement(i.Arguments[0], JsonSerializerOptions.Web).EnumerateArray().ToArray();
        var backlog = _inbox.Items.Select(n => (Key(n), (string?)null)).ToList();
        JSInterop.Setup<string[]?>("solo.notifications.claim", i => Sent(i).Length == 2).SetResult([]);
        var shownByApp = Entry(Guid.NewGuid(), TaskId, read: false) with { Title = "Shown while closed", Key = "DueThisWeek:x:2027-03-31" };
        var news = Entry(Guid.NewGuid(), OtherTaskId, read: false) with { Title = "Not shown yet" };
        JSInterop.Setup<string[]?>("solo.notifications.claim", i => Sent(i).Length == 4).SetResult([Key(news)]);

        var store = Store(inApp: true);
        await store.LoadAsync();
        _inbox = _inbox with { Items = [news, shownByApp, .. _inbox.Items], UnreadCount = 4 };
        await store.RefreshAsync();

        // The first read records the backlog with the app, unannounced; a
        // deadline entry names the reminder it was raised from.
        var claims = JSInterop.Invocations["solo.notifications.claim"];
        Assert.Equal(backlog, Sent(claims[0]).Select(e => (e.GetProperty("key").GetString()!, e.GetProperty("reminder").GetString())));
        Assert.Contains(Sent(claims[1]), e => e.GetProperty("reminder").GetString() == "DueThisWeek:x:2027-03-31");
        Assert.Equal(["Not shown yet"], Services.GetRequiredService<ISnackbar>().ShownSnackbars.Select(s => s.Message));
    }

    [Fact]
    public async Task A_tapped_reminder_opens_its_task_and_reads_its_unread_entries()
    {
        var store = Store(inApp: true);
        await store.LoadAsync();
        var mine = _inbox.Items.Single(n => n.TaskId == TaskId);
        var other = _inbox.Items.Single(n => n.TaskId == OtherTaskId);

        await store.OpenTaskFromNotification(TaskId.ToString());

        Assert.Equal([TaskId], _drawer.Opened);
        Assert.Contains($"POST /api/notifications/{mine.Id}/read", _http.Paths);
        Assert.DoesNotContain($"POST /api/notifications/{other.Id}/read", _http.Paths);

        await store.OpenTaskFromNotification("not-a-task");
        Assert.Equal([TaskId], _drawer.Opened);
    }

    [Fact]
    public async Task A_tapped_entry_raised_since_the_last_read_is_fetched_then_opened()
    {
        var store = Store(inApp: true);
        await store.LoadAsync();
        var raised = Entry(Guid.NewGuid(), OtherTaskId, read: false);
        _inbox = _inbox with { Items = [raised, .. _inbox.Items], UnreadCount = 3 };

        await store.OpenFromNotification(raised.Id.ToString());

        Assert.Equal([OtherTaskId], _drawer.Opened);
        Assert.Contains($"POST /api/notifications/{raised.Id}/read", _http.Paths);
    }

    private sealed class FakeDrawer : ISoloTaskDrawer
    {
        public List<Guid> Opened { get; } = [];

        public Task OpenAsync(Guid taskId, CancellationToken ct = default)
        {
            Opened.Add(taskId);
            return Task.CompletedTask;
        }

        public event Action<TaskDrawerChange>? Changed { add { } remove { } }

        public event Action? Closed { add { } remove { } }
    }

    private sealed class FakeTopics : ISoloLiveTopics
    {
        public bool IsConnected => true;

        public event Func<Task>? ConnectionChanged { add { } remove { } }

        public Task<LiveSendResult> SendAsync(Guid projectId, string topic, string kind, string? data = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IAsyncDisposable> SubscribeAsync(Guid projectId, string topic, Func<TopicMessage, Task> onMessage,
            Func<IReadOnlyList<TopicPresenceEntry>, Task>? onPresence = null) => throw new NotSupportedException();

        public Task SetPresenceAsync(Guid projectId, string topic, PresenceMode mode) => Task.CompletedTask;

        public Task<IAsyncDisposable> SubscribeUserAsync(string topic, Func<TopicMessage, Task> onMessage) =>
            Task.FromResult<IAsyncDisposable>(new Nothing());

        private sealed class Nothing : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
