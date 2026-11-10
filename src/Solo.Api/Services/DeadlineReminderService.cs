using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Api.Services;

/// <summary>
/// Reminds each assignee of an unfinished task on an open board twice: two
/// weeks before its deadline and one week before, at <see cref="ReminderHour"/>
/// in the assignee's own time zone.
/// </summary>
/// <remarks>
/// <para>Deadlines here are planning dates, not appointments, so the reminders
/// come early enough to change the plan rather than on the day. A task that gets
/// its deadline, or its assignee, inside one of those windows gets that window's
/// reminder on the next sweep, worded with the days actually left; one that is
/// already a week out skips the two-week one.</para>
///
/// <para>A deadline is a date with no time of day, so the hour is a choice
/// rather than anything in the data. The sweep is idempotent through the dedupe
/// key, which is what lets it simply run every few minutes and catch up after a
/// restart: a reminder missed at 9:00 goes out on the first sweep after.</para>
/// </remarks>
public sealed class DeadlineReminderService(
    IServiceScopeFactory scopes,
    NotificationService notifications,
    TimeProvider time,
    ILogger<DeadlineReminderService> logger)
    : BackgroundService
{
    public const int ReminderHour = 9;
    public const int FirstReminderDays = 14;
    public const int SecondReminderDays = 7;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

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
                logger.LogError(e, "Deadline reminder sweep failed; retrying next interval.");
            }

            await Task.Delay(Interval, time, stoppingToken);
        }
    }

    public async Task SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();

        // Every time zone's "today" lies within a day of UTC's, so this window
        // holds every deadline that is inside a reminder window for somebody.
        var utcToday = DateOnly.FromDateTime(now.UtcDateTime);
        var from = utcToday.AddDays(-1);
        var to = utcToday.AddDays(FirstReminderDays + 1);
        var due = await db.Assignments.AsNoTracking()
            .Where(a => a.Task != null &&
                        a.Task.DeadlineAt >= from && a.Task.DeadlineAt <= to &&
                        a.Task.Status != TaskState.Finished &&
                        a.Task.Board!.IsOpen &&
                        db.ProjectMembers.Any(m => m.ProjectId == a.Task.Board.ProjectId && m.UserId == a.UserId))
            .Select(a => new
            {
                a.UserId,
                TimeZone = db.NotificationPreferences
                    .Where(p => p.UserId == a.UserId).Select(p => p.TimeZone).FirstOrDefault(),
                TaskId = a.Task!.Id,
                a.Task.Name,
                a.Task.Number,
                Deadline = a.Task.DeadlineAt!.Value,
                a.Task.BoardId,
                BoardAlias = a.Task.Board!.Alias,
                BoardName = a.Task.Board.Name,
                a.Task.Board.ProjectId,
                ProjectAlias = a.Task.Board.Project!.Alias,
                ProjectName = a.Task.Board.Project.Name,
            })
            .ToListAsync(ct);

        var drafts = new List<NotificationDraft>();
        foreach (var row in due)
        {
            var local = TimeZoneInfo.ConvertTime(now, ZoneOf(row.TimeZone));
            if (local.Hour < ReminderHour)
            {
                continue;
            }

            var daysLeft = row.Deadline.DayNumber - DateOnly.FromDateTime(local.DateTime).DayNumber;
            NotificationKind? kind = daysLeft switch
            {
                < 0 or > FirstReminderDays => null,
                > SecondReminderDays => NotificationKind.DueInTwoWeeks,
                _ => NotificationKind.DueThisWeek,
            };
            if (kind is null)
            {
                continue;
            }

            drafts.Add(new NotificationDraft(
                row.UserId, kind.Value, Key(kind.Value, row.TaskId, row.Deadline),
                Title(Aliases.Serial(row.ProjectAlias, row.BoardAlias, row.Number), row.Name, daysLeft),
                Location(row.ProjectName, row.BoardName),
                row.ProjectId, row.BoardId, row.TaskId, null, null));
        }

        await notifications.RaiseAsync(drafts, ct);
    }

    /// <summary>
    /// The deadline reminders <paramref name="userId"/> will get after
    /// <paramref name="now"/>, as <see cref="SweepAsync"/> will raise them, so
    /// a device can schedule them ahead (the iPhone and iPad app, which shows
    /// them while it's closed). Same tasks as the sweep — assigned, unfinished,
    /// on an open board of a project the person is still in — and none already
    /// raised, so a reminder that went out early, worded with fewer days, isn't
    /// shown a second time. Empty when deadline notifications are off.
    /// </summary>
    public static async Task<IReadOnlyList<UpcomingReminderDto>> UpcomingAsync(
        SoloDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var preferences = await db.NotificationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (preferences is { NotifyOnDeadline: false })
        {
            return [];
        }

        var zone = ZoneOf(preferences?.TimeZone);
        var from = DateOnly.FromDateTime(now.UtcDateTime).AddDays(SecondReminderDays - 1);
        var tasks = await db.Assignments.AsNoTracking()
            .Where(a => a.UserId == userId && a.Task != null &&
                        a.Task.DeadlineAt >= from &&
                        a.Task.Status != TaskState.Finished &&
                        a.Task.Board!.IsOpen &&
                        db.ProjectMembers.Any(m => m.ProjectId == a.Task.Board.ProjectId && m.UserId == userId))
            .Select(a => new
            {
                TaskId = a.Task!.Id,
                a.Task.Name,
                a.Task.Number,
                Deadline = a.Task.DeadlineAt!.Value,
                BoardAlias = a.Task.Board!.Alias,
                BoardName = a.Task.Board.Name,
                ProjectAlias = a.Task.Board.Project!.Alias,
                ProjectName = a.Task.Board.Project.Name,
            })
            .ToListAsync(ct);

        var planned = tasks
            .SelectMany(t => Reminders.Select(r => (Task: t, r.Kind, r.DaysBefore,
                Key: Key(r.Kind, t.TaskId, t.Deadline), FireAt: ReminderAt(t.Deadline, r.DaysBefore, zone))))
            .Where(r => r.FireAt > now)
            .ToList();
        var keys = planned.Select(r => r.Key).ToList();
        var raised = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId && keys.Contains(n.DedupeKey))
            .Select(n => n.DedupeKey)
            .ToListAsync(ct);

        return planned
            .Where(r => !raised.Contains(r.Key))
            .OrderBy(r => r.FireAt)
            .Take(NotificationLimits.UpcomingLimit)
            .Select(r => new UpcomingReminderDto(
                r.Key, r.Kind, r.Task.TaskId, r.FireAt,
                Title(Aliases.Serial(r.Task.ProjectAlias, r.Task.BoardAlias, r.Task.Number), r.Task.Name, r.DaysBefore),
                Location(r.Task.ProjectName, r.Task.BoardName)))
            .ToList();
    }

    /// <summary>
    /// The reminders a deadline earns, soonest last: the dedupe key and the
    /// hour below are what <c>api/notifications/upcoming</c> plans with too, so
    /// a device scheduling one ahead and this sweep raising it agree.
    /// </summary>
    public static readonly IReadOnlyList<(NotificationKind Kind, int DaysBefore)> Reminders =
        [(NotificationKind.DueInTwoWeeks, FirstReminderDays), (NotificationKind.DueThisWeek, SecondReminderDays)];

    public static string Key(NotificationKind kind, Guid taskId, DateOnly deadline) =>
        $"{kind}:{taskId}:{deadline:yyyy-MM-dd}";

    public static string Title(string serial, string name, int daysLeft) => $"{serial} {name} is due {When(daysLeft)}";

    public static string Location(string projectName, string boardName) => $"{projectName} · {boardName}";

    /// <summary>
    /// When a reminder goes out: <see cref="ReminderHour"/> on the day
    /// <paramref name="daysBefore"/> before the deadline, in <paramref name="zone"/>.
    /// A 9:00 that a clock change skips becomes 10:00, which is when the sweep
    /// would first find it due.
    /// </summary>
    public static DateTimeOffset ReminderAt(DateOnly deadline, int daysBefore, TimeZoneInfo zone)
    {
        var local = deadline.AddDays(-daysBefore).ToDateTime(new TimeOnly(ReminderHour, 0));
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public static string When(int daysLeft) => daysLeft switch
    {
        0 => "today",
        1 => "tomorrow",
        FirstReminderDays => "in 2 weeks",
        SecondReminderDays => "in 1 week",
        _ => $"in {daysLeft} days",
    };

    /// <summary>The zone a stored id names, or UTC for none, or for one this host doesn't know.</summary>
    public static TimeZoneInfo ZoneOf(string? id) =>
        id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
}
