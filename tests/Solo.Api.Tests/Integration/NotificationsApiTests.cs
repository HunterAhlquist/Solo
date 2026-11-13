using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solo.Api.Services;
using Solo.Data;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The notify core: who gets an assignment or deadline notification, how
/// repeats are kept out, what the inbox shows, and the live nudge.
/// </summary>
[Collection("postgres")]
public sealed class NotificationsApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;
    private Guid _bobId;
    private ProjectDto _project = null!;
    private BoardDto _board = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("notifications"), [SoloApiFactory.FixtureDirectory]);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
        _bob = _factory.As("bob", "bob@example.com", "Bob");
        _project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Game", alias = "GAME" });
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members",
            new { email = "bob@example.com", role = "ReadWrite" });
        _board = await _admin.PostJson<BoardDto>($"/api/projects/{_project.Id}/boards", new { name = "Sprint", alias = "SP" });
        _bobId = (await _bob.GetJson<UserDto>("/api/users/me")).Id;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private Task<TaskDto> AssignAsync(Guid taskId, params Guid[] userIds) =>
        _admin.PutJson<TaskDto>($"/api/tasks/{taskId}/assignees", new { userIds });

    private Task<NotificationInboxDto> InboxAsync(HttpClient client) =>
        client.GetJson<NotificationInboxDto>("/api/notifications");

    /// <summary>A reminder's wording, as the catalog picks it for that key.</summary>
    private static (string Title, string Body) Reminder(
        NotificationKind kind, Guid taskId, DateOnly deadline, string serial, string name, int daysLeft)
    {
        var message = DeadlineReminderService.Message(
            kind, DeadlineReminderService.Key(kind, taskId, deadline), serial, name, daysLeft, "Game", "Sprint");
        return (message.Title, message.Body);
    }

    [SkippableFact]
    public async Task Assigning_NotifiesTheNewAssignee_NotTheAssigner_AndOnlyOnceWhileUnread()
    {
        postgres.SkipUnlessDockerAvailable();
        var adminId = (await _admin.GetJson<UserDto>("/api/users/me")).Id;
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{_board.Id}/tasks", new { name = "Boss fight" });

        await AssignAsync(task.Id, _bobId, adminId);
        var inbox = await InboxAsync(_bob);
        var only = Assert.Single(inbox.Items);
        Assert.Equal(1, inbox.UnreadCount);
        Assert.Equal(NotificationKind.Assigned, only.Kind);
        // One of the catalog's lines for an assignment, filled in.
        Assert.Contains(new NotificationMessages.Message(only.Title, only.Body!), NotificationMessages.Possible(
            NotificationMessages.Situation.Assigned,
            new Dictionary<string, string> { ["assigner"] = "Admin", ["task"] = "Boss fight", ["serial"] = "GAME-SP-1", ["project"] = "Game", ["board"] = "Sprint" }));
        Assert.Equal((task.Id, _board.Id, _project.Id), (only.TaskId, only.BoardId, only.ProjectId));
        Assert.Empty((await InboxAsync(_admin)).Items);

        // Off and on again while it's still unread: nothing new.
        await AssignAsync(task.Id);
        await AssignAsync(task.Id, _bobId);
        Assert.Single((await InboxAsync(_bob)).Items);

        // Once read, being handed it again is news.
        Assert.Equal(HttpStatusCode.NoContent, (await _bob.PostAsync($"/api/notifications/{only.Id}/read", null)).StatusCode);
        Assert.Equal(0, (await InboxAsync(_bob)).UnreadCount);
        await AssignAsync(task.Id);
        await AssignAsync(task.Id, _bobId);
        inbox = await InboxAsync(_bob);
        Assert.Equal(only.Id, Assert.Single(inbox.Items).Id);
        Assert.Equal(1, inbox.UnreadCount);

        // Someone else's notification is not Bob's to read.
        Assert.Equal(HttpStatusCode.NotFound,
            (await _admin.PostAsync($"/api/notifications/{only.Id}/read", null)).StatusCode);
    }

    [SkippableFact]
    public async Task EpicAssignments_Notify_AndTurningThemOff_Silences()
    {
        postgres.SkipUnlessDockerAvailable();
        var epic = await _admin.PostJson<EpicDto>($"/api/boards/{_board.Id}/epics", new { name = "Act one" });
        await _admin.PutJson<EpicDto>($"/api/epics/{epic.Id}/assignees", new { userIds = new[] { _bobId } });
        var note = Assert.Single((await InboxAsync(_bob)).Items);
        Assert.Contains(new NotificationMessages.Message(note.Title, note.Body!), NotificationMessages.Possible(
            NotificationMessages.Situation.AssignedEpic,
            new Dictionary<string, string> { ["assigner"] = "Admin", ["epic"] = "Act one", ["project"] = "Game", ["board"] = "Sprint" }));
        Assert.Equal((epic.Id, (Guid?)null), (note.EpicId, note.TaskId));

        var preferences = await _bob.GetJson<NotificationPreferencesDto>("/api/notifications/preferences");
        Assert.True(preferences.NotifyOnAssigned && preferences.NotifyOnDeadline);
        await _bob.PutJson<NotificationPreferencesDto>("/api/notifications/preferences", new
        {
            timeZone = "Europe/Berlin", notifyOnAssigned = false, notifyOnDeadline = true, preferences.Version,
        });
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{_board.Id}/tasks", new { name = "Quiet" });
        await AssignAsync(task.Id, _bobId);
        Assert.Single((await InboxAsync(_bob)).Items);

        // A stale version is a 409, like every other edit; a made-up zone is a 400.
        var stale = await _bob.PutAsJsonAsync("/api/notifications/preferences", new
        {
            notifyOnAssigned = true, notifyOnDeadline = true, preferences.Version,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await _bob.GetJson<NotificationPreferencesDto>("/api/notifications/preferences");
        Assert.Equal("Europe/Berlin", current.TimeZone);
        var unknown = await _bob.PutAsJsonAsync("/api/notifications/preferences", new
        {
            timeZone = "Mars/Olympus_Mons", notifyOnAssigned = true, notifyOnDeadline = true, current.Version,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [SkippableFact]
    public async Task DeadlineReminders_GoOutTwoWeeksAndOneWeekBefore_AtNineInTheAssigneesZone()
    {
        postgres.SkipUnlessDockerAvailable();
        var preferences = await _bob.GetJson<NotificationPreferencesDto>("/api/notifications/preferences");
        await _bob.PutJson<NotificationPreferencesDto>("/api/notifications/preferences", new
        {
            timeZone = "America/Los_Angeles", notifyOnAssigned = false, notifyOnDeadline = true, preferences.Version,
        });

        var deadline = new DateOnly(2027, 3, 31);
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{_board.Id}/tasks", new { name = "Ship demo", deadlineAt = deadline });
        var finished = await _admin.PostJson<TaskDto>($"/api/boards/{_board.Id}/tasks", new { name = "Done", deadlineAt = deadline });
        await AssignAsync(task.Id, _bobId);
        await AssignAsync(finished.Id, _bobId);
        await _admin.PutJson<TaskDto>($"/api/tasks/{finished.Id}/status", new { status = "Finished" });
        var sweeper = _factory.Services.GetRequiredService<DeadlineReminderService>();

        // 14 days out at 08:00 in Los Angeles (PDT, UTC-7): too early.
        await sweeper.SweepAsync(new DateTimeOffset(2027, 3, 17, 15, 0, 0, TimeSpan.Zero), default);
        Assert.Empty((await InboxAsync(_bob)).Items);

        // An hour later it goes out, once however many sweeps follow.
        await sweeper.SweepAsync(new DateTimeOffset(2027, 3, 17, 16, 0, 0, TimeSpan.Zero), default);
        await sweeper.SweepAsync(new DateTimeOffset(2027, 3, 18, 16, 0, 0, TimeSpan.Zero), default);
        var first = Assert.Single((await InboxAsync(_bob)).Items);
        Assert.Equal(NotificationKind.DueInTwoWeeks, first.Kind);
        Assert.Equal(Reminder(NotificationKind.DueInTwoWeeks, task.Id, deadline, "GAME-SP-1", "Ship demo", 14), (first.Title, first.Body!));

        await sweeper.SweepAsync(new DateTimeOffset(2027, 3, 24, 16, 0, 0, TimeSpan.Zero), default);
        var items = (await InboxAsync(_bob)).Items;
        Assert.Equal(2, items.Count);
        Assert.Equal(NotificationKind.DueThisWeek, items[0].Kind);
        Assert.Equal(Reminder(NotificationKind.DueThisWeek, task.Id, deadline, "GAME-SP-1", "Ship demo", 7), (items[0].Title, items[0].Body!));
        Assert.DoesNotContain(items, n => n.TaskId == finished.Id);

        // A deadline handed out inside the last week skips straight to that
        // window's reminder, worded with the days actually left.
        var late = await _admin.PostJson<TaskDto>($"/api/boards/{_board.Id}/tasks",
            new { name = "Rush", deadlineAt = new DateOnly(2027, 3, 27) });
        await AssignAsync(late.Id, _bobId);
        await sweeper.SweepAsync(new DateTimeOffset(2027, 3, 24, 17, 0, 0, TimeSpan.Zero), default);
        var rush = (await InboxAsync(_bob)).Items.Single(n => n.TaskId == late.Id);
        Assert.Equal(NotificationKind.DueThisWeek, rush.Kind);
        Assert.Equal(Reminder(NotificationKind.DueThisWeek, late.Id, new DateOnly(2027, 3, 27), "GAME-SP-3", "Rush", 3), (rush.Title, rush.Body!));
        Assert.Contains("in 3 days", rush.Title + rush.Body);
    }

    [SkippableFact]
    public async Task LeavingTheProject_HidesItsNotifications()
    {
        postgres.SkipUnlessDockerAvailable();
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{_board.Id}/tasks", new { name = "Secret" });
        await AssignAsync(task.Id, _bobId);
        Assert.Single((await InboxAsync(_bob)).Items);

        var members = await _admin.GetJson<List<ProjectMemberDto>>($"/api/projects/{_project.Id}/members");
        var bob = members.Single(m => m.Email == "bob@example.com");
        await _admin.DeleteAsync($"/api/projects/{_project.Id}/members/{bob.Id}");

        // Bob may have nothing left to sign in to, so read straight from the table.
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == _bobId));
        var other = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Other", alias = "OTH" });
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{other.Id}/members",
            new { email = "bob@example.com", role = "Read" });
        var inbox = await InboxAsync(_bob);
        Assert.Empty(inbox.Items);
        Assert.Equal(0, inbox.UnreadCount);
    }

    [SkippableFact]
    public async Task TheNotificationsTopic_NudgesOnlyTheRecipient()
    {
        postgres.SkipUnlessDockerAvailable();
        await using var bob = Connect("bob", "bob@example.com", "Bob");
        await using var admin = Connect("admin", "admin@example.com", "Admin");
        var heard = new TaskCompletionSource<TopicMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adminHeard = false;
        bob.On<TopicMessage>(nameof(IBoardClient.TopicMessage), m => heard.TrySetResult(m));
        admin.On<TopicMessage>(nameof(IBoardClient.TopicMessage), _ => adminHeard = true);
        foreach (var connection in new[] { bob, admin })
        {
            await connection.StartAsync();
            await connection.InvokeAsync(nameof(IBoardHubServer.SubscribeUserTopic), BoardHubConstants.NotificationsTopic);
        }

        var task = await _admin.PostJson<TaskDto>($"/api/boards/{_board.Id}/tasks", new { name = "Live" });
        await AssignAsync(task.Id, _bobId);
        var message = await heard.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var note = Assert.Single((await InboxAsync(_bob)).Items);
        Assert.Equal((BoardHubConstants.NotificationsTopic, nameof(NotificationKind.Assigned), note.Id.ToString()),
            (message.Topic, message.Kind, message.Data));

        await Task.Delay(300);
        Assert.False(adminHeard, "the assigner heard a notification meant for Bob");
    }

    [SkippableFact]
    public async Task MarkingRead_NudgesThePersonsOtherClients()
    {
        postgres.SkipUnlessDockerAvailable();
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{_board.Id}/tasks", new { name = "Elsewhere" });
        await AssignAsync(task.Id, _bobId);
        var note = Assert.Single((await InboxAsync(_bob)).Items);

        await using var otherDevice = Connect("bob", "bob@example.com", "Bob");
        var heard = new System.Collections.Concurrent.ConcurrentQueue<TopicMessage>();
        otherDevice.On<TopicMessage>(nameof(IBoardClient.TopicMessage), heard.Enqueue);
        await otherDevice.StartAsync();
        await otherDevice.InvokeAsync(nameof(IBoardHubServer.SubscribeUserTopic), BoardHubConstants.NotificationsTopic);

        (await _bob.PostAsync($"/api/notifications/{note.Id}/read", null)).EnsureSuccessStatusCode();
        await WaitUntilAsync(() => heard.Any(m => m.Data == note.Id.ToString()));

        var second = await _admin.PostJson<TaskDto>($"/api/boards/{_board.Id}/tasks", new { name = "Another" });
        await AssignAsync(second.Id, _bobId);
        heard.Clear();
        (await _bob.PostAsync("/api/notifications/read", null)).EnsureSuccessStatusCode();
        await WaitUntilAsync(() => heard.Any(m => m.Kind == "Read"));
        Assert.Equal(0, (await InboxAsync(_bob)).UnreadCount);
    }

    [SkippableFact]
    public async Task Clearing_EmptiesTheInbox_WithoutRaisingTheSameRemindersAgain()
    {
        postgres.SkipUnlessDockerAvailable();
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{_board.Id}/tasks",
            new { name = "Ship demo", deadlineAt = new DateOnly(2027, 3, 31) });
        await AssignAsync(task.Id, _bobId);
        var sweeper = _factory.Services.GetRequiredService<DeadlineReminderService>();
        await sweeper.SweepAsync(new DateTimeOffset(2027, 3, 17, 16, 0, 0, TimeSpan.Zero), default);
        Assert.Equal(2, (await InboxAsync(_bob)).Items.Count);

        await using var otherDevice = Connect("bob", "bob@example.com", "Bob");
        var heard = new System.Collections.Concurrent.ConcurrentQueue<TopicMessage>();
        otherDevice.On<TopicMessage>(nameof(IBoardClient.TopicMessage), heard.Enqueue);
        await otherDevice.StartAsync();
        await otherDevice.InvokeAsync(nameof(IBoardHubServer.SubscribeUserTopic), BoardHubConstants.NotificationsTopic);

        // Clearing is Bob's own: the admin's clear leaves his inbox alone.
        (await _admin.PostAsync("/api/notifications/clear", null)).EnsureSuccessStatusCode();
        Assert.Equal(2, (await InboxAsync(_bob)).Items.Count);

        (await _bob.PostAsync("/api/notifications/clear", null)).EnsureSuccessStatusCode();
        await WaitUntilAsync(() => heard.Any(m => m.Kind == "Read"));
        var inbox = await InboxAsync(_bob);
        Assert.Empty(inbox.Items);
        Assert.Equal(0, inbox.UnreadCount);

        await sweeper.SweepAsync(new DateTimeOffset(2027, 3, 18, 16, 0, 0, TimeSpan.Zero), default);
        Assert.Empty((await InboxAsync(_bob)).Items);

        // Being handed the task again is still news.
        await AssignAsync(task.Id);
        await AssignAsync(task.Id, _bobId);
        var again = Assert.Single((await InboxAsync(_bob)).Items);
        Assert.Equal(NotificationKind.Assigned, again.Kind);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the other client heard nothing");
            await Task.Delay(50);
        }
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
}
