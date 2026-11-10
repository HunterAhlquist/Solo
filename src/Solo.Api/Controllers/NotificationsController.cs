using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>
/// The signed-in person's own inbox and settings. No project in the route, so
/// no project policy: everything is scoped to the caller, and the inbox reads
/// through current membership, so leaving a project hides what it raised.
/// </summary>
[Route("api/notifications")]
[Authorize]
public sealed class NotificationsController(SoloDbContext db, CurrentUserAccessor currentUser, TimeProvider time)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<NotificationInboxDto>> Inbox(CancellationToken ct)
    {
        if (currentUser.UserId is not { } me)
        {
            return Forbid();
        }

        var visible = Visible(me);
        var items = await visible
            .OrderByDescending(n => n.CreatedAt)
            .Take(NotificationLimits.InboxPageSize)
            .ToListAsync(ct);
        var unread = await visible.CountAsync(n => n.ReadAt == null, ct);
        return new NotificationInboxDto(items.Select(n => n.ToDto()).ToList(), unread);
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        if (currentUser.UserId is not { } me)
        {
            return Forbid();
        }

        var now = time.GetUtcNow();
        var updated = await db.Notifications
            .Where(n => n.Id == id && n.UserId == me)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.ReadAt, n => n.ReadAt ?? now), ct);
        return updated == 0 ? NotFound() : NoContent();
    }

    [HttpPost("read")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        if (currentUser.UserId is not { } me)
        {
            return Forbid();
        }

        var now = time.GetUtcNow();
        await db.Notifications
            .Where(n => n.UserId == me && n.ReadAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.ReadAt, now), ct);
        return NoContent();
    }

    [HttpGet("preferences")]
    public async Task<ActionResult<NotificationPreferencesDto>> GetPreferences(CancellationToken ct)
    {
        if (currentUser.UserId is not { } me)
        {
            return Forbid();
        }

        return (await PreferencesAsync(me, ct)).ToDto();
    }

    [HttpPut("preferences")]
    public async Task<ActionResult<NotificationPreferencesDto>> UpdatePreferences(
        UpdateNotificationPreferencesRequest request, CancellationToken ct)
    {
        if (currentUser.UserId is not { } me)
        {
            return Forbid();
        }

        if (request.TimeZone is { } zone && !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown time zone",
                detail: $"\"{zone}\" isn't a time zone this server knows.");
        }

        var preferences = await PreferencesAsync(me, ct);
        UseClientVersion(db, preferences, request.Version);
        preferences.TimeZone = request.TimeZone;
        preferences.NotifyOnAssigned = request.NotifyOnAssigned;
        preferences.NotifyOnDeadline = request.NotifyOnDeadline;
        return await SaveOrConflictAsync(db, ct) ?? (ActionResult<NotificationPreferencesDto>)preferences.ToDto();
    }

    private IQueryable<Notification> Visible(Guid me) =>
        db.Notifications.AsNoTracking().Where(n =>
            n.UserId == me &&
            (n.ProjectId == null || db.ProjectMembers.Any(m => m.ProjectId == n.ProjectId && m.UserId == me)));

    /// <summary>The caller's row, made with the defaults on first read so there is always a version to send back.</summary>
    private async Task<NotificationPreferences> PreferencesAsync(Guid me, CancellationToken ct)
    {
        if (await db.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == me, ct) is { } existing)
        {
            return existing;
        }

        var created = new NotificationPreferences { UserId = me };
        db.NotificationPreferences.Add(created);
        try
        {
            await db.SaveChangesAsync(ct);
            return created;
        }
        catch (DbUpdateException)
        {
            // Two first reads at once (two tabs opening): the other one made it.
            db.Entry(created).State = EntityState.Detached;
            return await db.NotificationPreferences.FirstAsync(p => p.UserId == me, ct);
        }
    }
}
