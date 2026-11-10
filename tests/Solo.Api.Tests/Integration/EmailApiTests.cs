using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Solo.Api.Email;
using Solo.Data;
using Solo.Shared;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// Outgoing mail: the admin settings and their sealed password, invite emails,
/// the outbox's retries, digests and their schedule, and one-click unsubscribe.
/// </summary>
[Collection("postgres")]
public sealed class EmailApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private EmailFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _bob = null!;
    private ProjectDto _project = null!;
    private BoardDto _board = null!;
    private Guid _bobId;

    private sealed class FakeTransport : IEmailTransport
    {
        public List<(SmtpOptions Options, MimeMessage Message)> Sent { get; } = [];
        public bool Fail { get; set; }

        public Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct)
        {
            if (Fail)
            {
                throw new InvalidOperationException("550 mailbox unavailable");
            }

            Sent.Add((options, message));
            return Task.CompletedTask;
        }
    }

    private sealed class EmailFactory(string connectionString) : SoloApiFactory(connectionString)
    {
        public FakeTransport Transport { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IEmailTransport>(Transport));
        }
    }

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new EmailFactory(await postgres.CreateDatabaseAsync("email"));
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

    private static object Settings(bool enabled = true, string? password = null, string host = "smtp.example.com") => new
    {
        enabled,
        publicAddress = "https://solo.example.com",
        fromName = "Solo",
        fromAddress = "solo@example.com",
        smtpHost = host,
        smtpPort = 587,
        security = "StartTls",
        username = "solo",
        password,
        version = 0,
    };

    private Task<EmailSettingsDto> EnableEmailAsync() =>
        _admin.PutJson<EmailSettingsDto>("/api/settings/email", Settings(password: "hunter2"));

    private T Db<T>(Func<SoloDbContext, T> read)
    {
        using var scope = _factory.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<SoloDbContext>());
    }

    private Task SendAsync(DateTimeOffset? now = null) =>
        _factory.Services.GetRequiredService<EmailSender>().SendDueAsync(now ?? DateTimeOffset.UtcNow, default);

    private Task SweepAsync(DateTimeOffset now) =>
        _factory.Services.GetRequiredService<DigestService>().SweepAsync(now, default);

    private async Task<NotificationPreferencesDto> SetBobsDigestAsync(
        DigestFrequency frequency, string? zone = null, int hour = 8, DayOfWeek weekday = DayOfWeek.Monday, bool notifyOnAssigned = true)
    {
        var current = await _bob.GetJson<NotificationPreferencesDto>("/api/notifications/preferences");
        return await _bob.PutJson<NotificationPreferencesDto>("/api/notifications/preferences", new
        {
            timeZone = zone,
            notifyOnAssigned,
            notifyOnDeadline = true,
            emailOnAssigned = true,
            emailOnDeadline = true,
            digestFrequency = frequency.ToString(),
            digestHour = hour,
            digestWeekday = weekday.ToString(),
            version = current.Version,
        });
    }

    private async Task<TaskDto> AssignedToBobAsync(string name, DateOnly? deadline = null)
    {
        var task = await _admin.PostJson<TaskDto>($"/api/boards/{_board.Id}/tasks", new { name, deadlineAt = deadline });
        return await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = new[] { _bobId } });
    }

    /// <summary>Tomorrow, so notifications raised now still count as recent when the sweep runs "then".</summary>
    private static DateOnly Tomorrow => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);

    private static DateTimeOffset At(DateOnly day, int hour, int minute, string zone = "UTC") =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, minute)), TimeZoneInfo.FindSystemTimeZoneById(zone)), TimeSpan.Zero);

    [SkippableFact]
    public async Task Settings_SealThePassword_NeverReturnIt_AndForgetItWhenTheServerChanges()
    {
        postgres.SkipUnlessDockerAvailable();

        var incomplete = await _admin.PutAsJsonAsync("/api/settings/email", new { enabled = true, smtpPort = 587, version = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _bob.GetAsync("/api/settings/email")).StatusCode);

        var saved = await EnableEmailAsync();
        Assert.True(saved.HasPassword);
        Assert.False(saved.PasswordFromEnvironment);
        var stored = Db(db => db.EmailSettings.Single().ProtectedPassword);
        Assert.NotNull(stored);
        Assert.DoesNotContain("hunter2", stored);
        Assert.DoesNotContain("hunter2", await _admin.GetStringAsync("/api/settings/email"));
        Assert.True((await _admin.GetJson<AuthConfigDto>("/api/config/auth")).EmailEnabled);

        // Same server, no password sent: kept. Another server: dropped.
        var kept = await _admin.PutJson<EmailSettingsDto>("/api/settings/email",
            new { enabled = true, publicAddress = "https://solo.example.com", fromAddress = "solo@example.com", smtpHost = "smtp.example.com", smtpPort = 465, security = "SslOnConnect", version = saved.Version });
        Assert.True(kept.HasPassword);
        var moved = await _admin.PutJson<EmailSettingsDto>("/api/settings/email",
            new { enabled = true, publicAddress = "https://solo.example.com", fromAddress = "solo@example.com", smtpHost = "smtp.elsewhere.example", smtpPort = 465, security = "SslOnConnect", version = kept.Version });
        Assert.False(moved.HasPassword);
    }

    [SkippableFact]
    public async Task TestEmail_GoesToTheAdmin_WithTheFormsValues_AndReportsTheServersError()
    {
        postgres.SkipUnlessDockerAvailable();

        var result = await _admin.PostJson<TestEmailResultDto>("/api/settings/email/test", Settings(enabled: false, password: "typed"));
        Assert.Equal("admin@example.com", result.SentTo);
        var (options, message) = Assert.Single(_factory.Transport.Sent);
        Assert.Equal(("smtp.example.com", "typed"), (options.Host, options.Password));
        Assert.Equal("Solo test email", message.Subject);
        Assert.False(Db(db => db.EmailSettings.Any()));

        _factory.Transport.Fail = true;
        var failed = await _admin.PostAsJsonAsync("/api/settings/email/test", Settings(enabled: false));
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        Assert.Contains("550 mailbox unavailable", await failed.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Invites_AreEmailedOnlyWhileEmailIsOn_AndCanBeResent()
    {
        postgres.SkipUnlessDockerAvailable();

        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members", new { email = "carol@example.com", role = "Read" });
        Assert.Equal(0, Db(db => db.EmailMessages.Count()));

        await EnableEmailAsync();
        var dave = await _admin.PostJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members", new { email = "dave@example.com", role = "Read" });
        var invite = Db(db => db.EmailMessages.Single());
        Assert.Equal(("dave@example.com", "invite"), (invite.To, invite.Kind));
        Assert.Equal("Admin invited you to Game on Solo", invite.Subject);
        Assert.Contains("Sign in with the account for dave@example.com to see it.", invite.TextBody);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"/api/projects/{_project.Id}/members/{dave.Id}/invite", null)).StatusCode);
        Assert.Equal(2, Db(db => db.EmailMessages.Count(m => m.To == "dave@example.com")));
        var bob = (await _admin.GetJson<List<ProjectMemberDto>>($"/api/projects/{_project.Id}/members")).Single(m => m.Email == "bob@example.com");
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PostAsync($"/api/projects/{_project.Id}/members/{bob.Id}/invite", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _bob.PostAsync($"/api/projects/{_project.Id}/members/{dave.Id}/invite", null)).StatusCode);

        await SendAsync();
        Assert.Equal(2, _factory.Transport.Sent.Count);
        var sent = _factory.Transport.Sent[0].Message;
        Assert.Equal("solo@example.com", sent.From.Mailboxes.Single().Address);
        Assert.Contains(sent.BodyParts.OfType<MimePart>(), p => p.ContentId == EmailTemplates.MarkContentId);
        Assert.All(Db(db => db.EmailMessages.ToList()), m => Assert.NotNull(m.SentAt));
    }

    [SkippableFact]
    public async Task FailedSends_AreRetried_ThenGivenUpOn_AndShownToTheAdmin()
    {
        postgres.SkipUnlessDockerAvailable();
        await EnableEmailAsync();
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members", new { email = "dave@example.com", role = "Read" });
        _factory.Transport.Fail = true;

        var now = DateTimeOffset.UtcNow;
        foreach (var delay in EmailSender.RetryDelays)
        {
            await SendAsync(now);
            var waiting = Db(db => db.EmailMessages.Single());
            Assert.Null(waiting.FailedAt);
            Assert.Equal(now + delay, waiting.NextAttemptAt, TimeSpan.FromMilliseconds(1));
            now += delay;
        }

        await SendAsync(now);
        var failed = Db(db => db.EmailMessages.Single());
        Assert.Equal(5, failed.Attempts);
        Assert.NotNull(failed.FailedAt);
        var shown = Assert.Single((await _admin.GetJson<EmailSettingsDto>("/api/settings/email")).RecentFailures);
        Assert.Equal(("dave@example.com", "550 mailbox unavailable"), (shown.To, shown.Error));
    }

    [SkippableFact]
    public async Task DailyDigest_GoesOutAtTheirHour_InTheirZone_WithOverdueDueSoonAndNewAssignments()
    {
        postgres.SkipUnlessDockerAvailable();
        const string zone = "America/Los_Angeles";
        var day = Tomorrow;
        await EnableEmailAsync();
        await SetBobsDigestAsync(DigestFrequency.Daily, zone);
        await AssignedToBobAsync("Late", day.AddDays(-2));
        await AssignedToBobAsync("Soon", day.AddDays(3));
        await AssignedToBobAsync("Someday", day.AddDays(20));
        var read = await AssignedToBobAsync("Already seen");
        var seen = (await _bob.GetJson<NotificationInboxDto>("/api/notifications")).Items.Single(n => n.TaskId == read.Id);
        await _bob.PostAsync($"/api/notifications/{seen.Id}/read", null);

        await SweepAsync(At(day, 7, 59, zone));
        Assert.Equal(0, Db(db => db.EmailMessages.Count()));

        await SweepAsync(At(day, 8, 5, zone));
        var digest = Db(db => db.EmailMessages.Single());
        Assert.Equal(("bob@example.com", "Your daily Solo digest"), (digest.To, digest.Subject));
        var text = digest.TextBody;
        Assert.True(text.IndexOf("Overdue", StringComparison.Ordinal) < text.IndexOf("Due in the next 2 weeks", StringComparison.Ordinal));
        Assert.Contains("- GAME-SP-1 Late (Game · Sprint), 2 days late: https://solo.example.com/projects/GAME/boards/SP?task=1", text);
        Assert.Contains("GAME-SP-2 Soon (Game · Sprint), ", text);
        Assert.Contains("in 3 days", text);
        Assert.DoesNotContain("Someday", text.Split("Recently assigned to you")[0]);
        Assert.Contains("Admin assigned you to GAME-SP-3 Someday", text);
        Assert.DoesNotContain("Already seen", text);
        Assert.Contains("Change them or turn the digest off: https://solo.example.com/notifications", text);
        Assert.StartsWith("https://solo.example.com/api/email/unsubscribe?token=", digest.UnsubscribeUrl);

        // Once a day, and what it carried doesn't come round again tomorrow.
        await SweepAsync(At(day, 9, 0, zone));
        Assert.Equal(1, Db(db => db.EmailMessages.Count()));
        Assert.DoesNotContain(Db(db => db.Notifications.Where(n => n.UserId == _bobId && n.ReadAt == null).ToList()), n => n.EmailedAt is null);
    }

    [SkippableFact]
    public async Task WeeklyDigest_WaitsForTheirWeekday_AndAQuietOneIsNeverSent()
    {
        postgres.SkipUnlessDockerAvailable();
        var day = Tomorrow;
        await EnableEmailAsync();
        await SetBobsDigestAsync(DigestFrequency.Weekly, weekday: day.AddDays(1).DayOfWeek);
        await SweepAsync(At(day, 10, 0));
        Assert.Equal(0, Db(db => db.EmailMessages.Count()));

        // Its day, but nothing to say.
        await SweepAsync(At(day.AddDays(1), 10, 0));
        Assert.Equal(0, Db(db => db.EmailMessages.Count()));

        await AssignedToBobAsync("Soon", day.AddDays(9));
        await SweepAsync(At(day.AddDays(1), 11, 0));
        Assert.Equal(0, Db(db => db.EmailMessages.Count()));
        await SweepAsync(At(day.AddDays(8), 10, 0));
        Assert.Equal("Your weekly Solo digest", Db(db => db.EmailMessages.Single()).Subject);
    }

    [SkippableFact]
    public async Task AsItHappens_BatchesNewNotifications_AtMostEveryFifteenMinutes()
    {
        postgres.SkipUnlessDockerAvailable();
        await EnableEmailAsync();
        await SetBobsDigestAsync(DigestFrequency.AsItHappens);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);

        await AssignedToBobAsync("First");
        await SweepAsync(now);
        Assert.Equal("As it happens: 1 new on Solo", Db(db => db.EmailMessages.Single()).Subject);

        await AssignedToBobAsync("Second");
        await AssignedToBobAsync("Third");
        await SweepAsync(now.AddMinutes(5));
        Assert.Equal(1, Db(db => db.EmailMessages.Count()));
        await SweepAsync(now.AddMinutes(16));
        Assert.Equal("As it happens: 2 new on Solo", Db(db => db.EmailMessages.OrderBy(m => m.CreatedAt).Last()).Subject);
    }

    [SkippableFact]
    public async Task EmailOnlyAssignments_SkipTheInbox_ButReachTheDigest_AgainWhenReassigned()
    {
        postgres.SkipUnlessDockerAvailable();
        await EnableEmailAsync();
        await SetBobsDigestAsync(DigestFrequency.AsItHappens, notifyOnAssigned: false);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);

        var task = await AssignedToBobAsync("Quiet");
        var inbox = await _bob.GetJson<NotificationInboxDto>("/api/notifications");
        Assert.Equal((0, 0), (inbox.Items.Count, inbox.UnreadCount));
        await SweepAsync(now);
        Assert.Contains("Admin assigned you to GAME-SP-1 Quiet", Db(db => db.EmailMessages.Single()).TextBody);

        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = Array.Empty<Guid>() });
        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = new[] { _bobId } });
        await SweepAsync(now.AddMinutes(16));
        Assert.Equal(2, Db(db => db.EmailMessages.Count()));
    }

    [SkippableFact]
    public async Task AReadAssignment_GivenAgain_IsEmailedAgain()
    {
        postgres.SkipUnlessDockerAvailable();
        await EnableEmailAsync();
        await SetBobsDigestAsync(DigestFrequency.AsItHappens);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);

        var task = await AssignedToBobAsync("Again");
        await SweepAsync(now);
        var note = Assert.Single((await _bob.GetJson<NotificationInboxDto>("/api/notifications")).Items);
        await _bob.PostAsync($"/api/notifications/{note.Id}/read", null);
        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = Array.Empty<Guid>() });
        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/assignees", new { userIds = new[] { _bobId } });

        await SweepAsync(now.AddMinutes(16));
        Assert.Equal(2, Db(db => db.EmailMessages.Count()));
    }

    [SkippableFact]
    public async Task DigestLinks_FollowATaskToTheBoardItMovedTo()
    {
        postgres.SkipUnlessDockerAvailable();
        await EnableEmailAsync();
        await SetBobsDigestAsync(DigestFrequency.AsItHappens);
        var next = await _admin.PostJson<BoardDto>($"/api/projects/{_project.Id}/boards", new { name = "Next", alias = "NEXT" });
        await _admin.PostJson<TaskDto>($"/api/boards/{next.Id}/tasks", new { name = "Already there" });

        var task = await AssignedToBobAsync("Moving");
        await _admin.PutJson<TaskDto>($"/api/tasks/{task.Id}/board", new { boardId = next.Id });
        await SweepAsync(DateTimeOffset.UtcNow.AddSeconds(1));

        var text = Db(db => db.EmailMessages.Single()).TextBody;
        Assert.Contains("https://solo.example.com/projects/GAME/boards/NEXT?task=2", text);
        Assert.DoesNotContain("/boards/SP", text);
    }

    [SkippableFact]
    public async Task Unsubscribe_TurnsTheDigestOff_WithTheLinksTokenAlone()
    {
        postgres.SkipUnlessDockerAvailable();
        await EnableEmailAsync();
        await SetBobsDigestAsync(DigestFrequency.AsItHappens);
        await AssignedToBobAsync("First");
        await SweepAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        var url = new Uri(Db(db => db.EmailMessages.Single()).UnsubscribeUrl!);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsync("/api/email/unsubscribe?token=forged", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync(url.PathAndQuery, null)).StatusCode);
        Assert.Equal(DigestFrequency.Off, (await _bob.GetJson<NotificationPreferencesDto>("/api/notifications/preferences")).DigestFrequency);
    }

    [SkippableFact]
    public async Task NothingIsDigested_WhileEmailIsOff()
    {
        postgres.SkipUnlessDockerAvailable();
        await SetBobsDigestAsync(DigestFrequency.AsItHappens);
        await AssignedToBobAsync("First");
        await SweepAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(0, Db(db => db.EmailMessages.Count()));
        Assert.All(Db(db => db.Notifications.ToList()), n => Assert.Null(n.EmailedAt));
    }
}
