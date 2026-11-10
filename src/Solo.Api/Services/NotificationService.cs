using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Realtime;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Api.Services;

/// <summary>What a trigger wants raised; the service decides who actually gets it.</summary>
public sealed record NotificationDraft(
    Guid UserId,
    NotificationKind Kind,
    string DedupeKey,
    string Title,
    string? Body,
    Guid? ProjectId,
    Guid? BoardId,
    Guid? TaskId,
    Guid? EpicId,
    Guid? ActorUserId);

/// <summary>The task or epic an assignment, or a reminder, is about.</summary>
public sealed record NotifiedItem(
    Guid ProjectId, Guid BoardId, Guid? TaskId, Guid? EpicId, string Name, string Where);

/// <summary>
/// The one place notifications are raised: every trigger hands its drafts here,
/// and every channel reads the rows this writes.
/// </summary>
/// <remarks>
/// <para>Best-effort, like the board broadcasts: it runs after the change it
/// reports has committed, in its own scope, and a failure is logged rather than
/// thrown, so a notification can never fail the write that caused it.</para>
///
/// <para>Dedupe is by key per person. A key already held is dropped, except an
/// assignment the person has already read, which comes back to the top as new:
/// being handed the same task again after letting it go is news, a second
/// assignment while the first is still unread is not.</para>
/// </remarks>
public sealed class NotificationService(
    IServiceScopeFactory scopes,
    IHubContext<BoardHub, IBoardClient> hub,
    TimeProvider time,
    ILogger<NotificationService> logger)
{
    public static string AssignedKey(NotifiedItem item) =>
        item.TaskId is { } taskId ? $"assigned:task:{taskId}" : $"assigned:epic:{item.EpicId}";

    public Task AssignedAsync(IEnumerable<Guid> userIds, User actor, NotifiedItem item, CancellationToken ct)
    {
        var actorName = actor.DisplayName ?? actor.Email;
        var drafts = userIds
            .Where(id => id != actor.Id)
            .Select(id => new NotificationDraft(
                id, NotificationKind.Assigned, AssignedKey(item),
                $"{actorName} assigned you to {item.Name}", item.Where,
                item.ProjectId, item.BoardId, item.TaskId, item.EpicId, actor.Id))
            .ToList();
        return RaiseAsync(drafts, ct);
    }

    public async Task RaiseAsync(IReadOnlyCollection<NotificationDraft> drafts, CancellationToken ct)
    {
        if (drafts.Count == 0)
        {
            return;
        }

        try
        {
            var raised = await SaveAsync(drafts, ct);
            await Task.WhenAll(raised.Where(n => n.InApp).Select(n => NudgeAsync(n.UserId, n.Kind.ToString(), n.Id.ToString(), n.ActorUserId ?? Guid.Empty)));
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(e, "Raising {Count} notifications failed; ignored.", drafts.Count);
        }
    }

    /// <summary>
    /// Read state lives on the server, so the person's other open clients
    /// refetch too; the nudge names nothing new, so it alerts nobody.
    /// </summary>
    public async Task ReadChangedAsync(Guid userId, string? id)
    {
        try
        {
            await NudgeAsync(userId, "Read", id, userId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Couldn't tell {UserId}'s other clients about a read change.", userId);
        }
    }

    private Task NudgeAsync(Guid userId, string kind, string? id, Guid actorUserId) =>
        hub.Clients.Group(BoardHubConstants.UserTopicGroup(userId, BoardHubConstants.NotificationsTopic))
            .TopicMessage(new TopicMessage(Guid.Empty, BoardHubConstants.NotificationsTopic, kind, id, actorUserId));

    private async Task<IReadOnlyList<Notification>> SaveAsync(IReadOnlyCollection<NotificationDraft> drafts, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();

        var userIds = drafts.Select(d => d.UserId).Distinct().ToList();
        var preferences = await db.NotificationPreferences.AsNoTracking()
            .Where(p => userIds.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, ct);
        var wanted = drafts
            .Where(d => !preferences.TryGetValue(d.UserId, out var p) || WantsInApp(p, d.Kind) || WantsEmail(p, d.Kind))
            .DistinctBy(d => (d.UserId, d.DedupeKey))
            .ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var keys = wanted.Select(d => d.DedupeKey).Distinct().ToList();
        var existing = await db.Notifications
            .Where(n => userIds.Contains(n.UserId) && keys.Contains(n.DedupeKey))
            .ToDictionaryAsync(n => (n.UserId, n.DedupeKey), ct);

        var now = time.GetUtcNow();
        var raised = new List<Notification>();
        foreach (var draft in wanted)
        {
            if (!existing.TryGetValue((draft.UserId, draft.DedupeKey), out var row))
            {
                row = new Notification { Title = "", DedupeKey = draft.DedupeKey, UserId = draft.UserId };
                db.Notifications.Add(row);
            }
            else if (draft.Kind != NotificationKind.Assigned || !Seen(row))
            {
                continue;
            }

            row.Kind = draft.Kind;
            row.Title = Truncate(draft.Title, NotificationLimits.TitleLength);
            row.Body = draft.Body is null ? null : Truncate(draft.Body, NotificationLimits.BodyLength);
            row.ProjectId = draft.ProjectId;
            row.BoardId = draft.BoardId;
            row.TaskId = draft.TaskId;
            row.EpicId = draft.EpicId;
            row.ActorUserId = draft.ActorUserId;
            row.CreatedAt = now;
            row.ReadAt = null;
            row.EmailedAt = null;
            row.InApp = !preferences.TryGetValue(draft.UserId, out var p) || WantsInApp(p, draft.Kind);
            raised.Add(row);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e)
        {
            // Two saves raising the same key at once: the unique index let one
            // through, and that one is all the person needed.
            logger.LogInformation(e, "A concurrent save already raised one of these notifications.");
            return [];
        }

        return raised;
    }

    private static bool WantsInApp(NotificationPreferences preferences, NotificationKind kind) => kind switch
    {
        NotificationKind.Assigned => preferences.NotifyOnAssigned,
        NotificationKind.DueInTwoWeeks or NotificationKind.DueThisWeek => preferences.NotifyOnDeadline,
        _ => true,
    };

    /// <summary>Kept for the digest even when the app shouldn't show it.</summary>
    private static bool WantsEmail(NotificationPreferences preferences, NotificationKind kind) =>
        preferences.DigestFrequency != DigestFrequency.Off && kind switch
        {
            NotificationKind.Assigned => preferences.EmailOnAssigned,
            NotificationKind.DueInTwoWeeks or NotificationKind.DueThisWeek => preferences.EmailOnDeadline,
            _ => false,
        };

    /// <summary>An email-only entry is never read in the app, so being emailed is its "read".</summary>
    private static bool Seen(Notification row) => row.ReadAt is not null || (!row.InApp && row.EmailedAt is not null);

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "…";
}
