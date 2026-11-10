using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Shared;

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

            var serial = Aliases.Serial(row.ProjectAlias, row.BoardAlias, row.Number);
            drafts.Add(new NotificationDraft(
                row.UserId, kind.Value, $"{kind}:{row.TaskId}:{row.Deadline:yyyy-MM-dd}",
                $"{serial} {row.Name} is due {When(daysLeft)}", $"{row.ProjectName} · {row.BoardName}",
                row.ProjectId, row.BoardId, row.TaskId, null, null));
        }

        await notifications.RaiseAsync(drafts, ct);
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
