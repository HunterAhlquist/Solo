using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared;

namespace Solo.Api.Email;

/// <summary>
/// Sends each person their digest when it is due: daily or weekly at the hour
/// they picked in their own time zone, or "as it happens", batched at most every
/// 15 minutes.
/// </summary>
/// <remarks>
/// <para>A daily or weekly digest lists the person's overdue work and work due
/// within two weeks, earliest deadline first, and what they were assigned since
/// the last one. As it happens carries only the new notifications. Neither goes
/// out empty, and nothing already read in the app is repeated.</para>
///
/// <para>Like the reminder sweep, this simply runs every few minutes: the last
/// digest time is the cursor, so a restart catches up on the next sweep.</para>
/// </remarks>
public sealed class DigestService(
    IServiceScopeFactory scopes,
    EmailOutbox outbox,
    IDataProtectionProvider protection,
    TimeProvider time,
    ILogger<DigestService> logger) : BackgroundService
{
    public static readonly TimeSpan AsItHappensInterval = TimeSpan.FromMinutes(15);

    /// <summary>Notifications older than this stay out of email, so turning email on doesn't send a backlog.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);

    public const string UnsubscribePurpose = "Solo.Email.Unsubscribe";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IDataProtector _unsubscribe = protection.CreateProtector(UnsubscribePurpose);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so migrations finish first.
        await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(time.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Digest sweep failed; retrying next interval.");
            }

            await Task.Delay(Interval, time, stoppingToken);
        }
    }

    public async Task SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
        if (await EmailOutbox.ReadySettingsAsync(db, ct) is not { } settings)
        {
            return;
        }

        var instanceName = await EmailOutbox.InstanceNameAsync(db, ct);
        var people = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Email }).ToListAsync(ct);
        var queued = false;
        foreach (var person in people)
        {
            try
            {
                queued |= await DigestAsync(db, settings, instanceName, person.Id, person.Email, now, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // They changed their settings mid-sweep; the next sweep reads the new ones.
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        if (queued)
        {
            outbox.Wake();
        }
    }

    /// <summary>Unsubscribe links carry the person's id, sealed so only the recipient can use theirs.</summary>
    public Guid? UserFromUnsubscribeToken(string token)
    {
        try
        {
            return Guid.TryParse(_unsubscribe.Unprotect(token), out var id) ? id : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private async Task<bool> DigestAsync(
        SoloDbContext db, EmailSettings settings, string instanceName, Guid userId, string email, DateTimeOffset now, CancellationToken ct)
    {
        var preferences = await db.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (preferences is null)
        {
            preferences = new NotificationPreferences { UserId = userId };
            db.NotificationPreferences.Add(preferences);
        }

        var zone = DeadlineReminderService.ZoneOf(preferences.TimeZone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        if (!IsDue(preferences, now, local, today, zone))
        {
            return false;
        }

        var notes = await NewNotificationsAsync(db, userId, now, ct);
        foreach (var note in notes)
        {
            note.Row.EmailedAt = now;
        }

        var root = EmailOutbox.BaseAddress(settings);
        var settingsLink = root + "notifications";
        EmailContent? content;
        if (preferences.DigestFrequency == DigestFrequency.AsItHappens)
        {
            var wanted = notes
                .Where(n => n.Row.Kind == NotificationKind.Assigned ? preferences.EmailOnAssigned : preferences.EmailOnDeadline)
                .Select(n => n.Note(root))
                .ToList();
            content = wanted.Count == 0 ? null : EmailTemplates.AsItHappens(instanceName, root, settingsLink, wanted);
        }
        else
        {
            var tasks = preferences.EmailOnDeadline ? await DueTasksAsync(db, userId, today, root, ct) : [];
            var assigned = preferences.EmailOnAssigned
                ? notes.Where(n => n.Row.Kind == NotificationKind.Assigned).Select(n => n.Note(root)).ToList()
                : [];
            var overdue = tasks.Where(t => t.DaysLeft < 0).ToList();
            var dueSoon = tasks.Where(t => t.DaysLeft >= 0).ToList();
            content = overdue.Count + dueSoon.Count + assigned.Count == 0 ? null
                : EmailTemplates.Digest(instanceName, preferences.DigestFrequency == DigestFrequency.Weekly, today,
                    root, settingsLink, overdue, dueSoon, assigned);

            // Considered, sent or not: a quiet day doesn't get another look an hour later.
            preferences.LastDigestAt = now;
        }

        if (content is not null)
        {
            var unsubscribe = $"{root}api/email/unsubscribe?token={Uri.EscapeDataString(_unsubscribe.Protect(userId.ToString()))}";
            EmailOutbox.Queue(db, email, content, "digest", unsubscribe);
            preferences.LastDigestAt = now;
        }

        await db.SaveChangesAsync(ct);
        return content is not null;
    }

    private static bool IsDue(NotificationPreferences preferences, DateTimeOffset now, DateTimeOffset local, DateOnly today, TimeZoneInfo zone)
    {
        var sentToday = preferences.LastDigestAt is { } last &&
                        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(last, zone).DateTime) == today;
        return preferences.DigestFrequency switch
        {
            DigestFrequency.AsItHappens => preferences.LastDigestAt is not { } previous || now - previous >= AsItHappensInterval,
            DigestFrequency.Daily => local.Hour >= preferences.DigestHour && !sentToday,
            DigestFrequency.Weekly => local.DayOfWeek == preferences.DigestWeekday && local.Hour >= preferences.DigestHour && !sentToday,
            _ => false,
        };
    }

    private sealed record NewNotification(Notification Row, int? TaskNumber, string? BoardAlias, string? ProjectAlias)
    {
        public DigestNote Note(string root) => new(Row.Title, Row.Body,
            ProjectAlias is null || BoardAlias is null ? root
            : TaskNumber is { } number ? $"{root}projects/{ProjectAlias}/boards/{BoardAlias}?task={number}"
            : $"{root}projects/{ProjectAlias}/boards/{BoardAlias}");
    }

    /// <summary>Unread, not yet emailed, recent, and from a project the person still belongs to.</summary>
    private static async Task<List<NewNotification>> NewNotificationsAsync(SoloDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - RecentWindow;
        var rows = await db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null && n.EmailedAt == null && n.CreatedAt >= since && n.CreatedAt <= now &&
                        (n.ProjectId == null || db.ProjectMembers.Any(m => m.ProjectId == n.ProjectId && m.UserId == userId)))
            .OrderBy(n => n.CreatedAt)
            .Select(n => new
            {
                Row = n,
                TaskNumber = db.Tasks.Where(t => t.Id == n.TaskId).Select(t => (int?)t.Number).FirstOrDefault(),
                BoardAlias = db.Boards.Where(b => b.Id == n.BoardId).Select(b => b.Alias).FirstOrDefault(),
                ProjectAlias = db.Projects.Where(p => p.Id == n.ProjectId).Select(p => p.Alias).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return rows.Select(r => new NewNotification(r.Row, r.TaskNumber, r.BoardAlias, r.ProjectAlias)).ToList();
    }

    /// <summary>The person's unfinished work on open boards, overdue or due within two weeks, earliest first.</summary>
    private static async Task<List<DigestTask>> DueTasksAsync(SoloDbContext db, Guid userId, DateOnly today, string root, CancellationToken ct)
    {
        var horizon = today.AddDays(DeadlineReminderService.FirstReminderDays);
        var rows = await db.Assignments.AsNoTracking()
            .Where(a => a.UserId == userId && a.Task != null &&
                        a.Task.DeadlineAt <= horizon &&
                        a.Task.Status != TaskState.Finished &&
                        a.Task.Board!.IsOpen &&
                        db.ProjectMembers.Any(m => m.ProjectId == a.Task.Board.ProjectId && m.UserId == userId))
            .Select(a => new
            {
                a.Task!.Name,
                a.Task.Number,
                Deadline = a.Task.DeadlineAt!.Value,
                BoardAlias = a.Task.Board!.Alias,
                BoardName = a.Task.Board.Name,
                ProjectAlias = a.Task.Board.Project!.Alias,
                ProjectName = a.Task.Board.Project.Name,
            })
            .OrderBy(t => t.Deadline).ThenBy(t => t.Number)
            .ToListAsync(ct);
        return rows.Select(t => new DigestTask(
            $"{Aliases.Serial(t.ProjectAlias, t.BoardAlias, t.Number)} {t.Name}",
            $"{t.ProjectName} · {t.BoardName}",
            $"{root}projects/{t.ProjectAlias}/boards/{t.BoardAlias}?task={t.Number}",
            t.Deadline,
            t.Deadline.DayNumber - today.DayNumber)).ToList();
    }
}
