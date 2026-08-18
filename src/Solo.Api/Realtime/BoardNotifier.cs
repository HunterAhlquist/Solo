using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Data;
using Solo.Extensibility;
using Solo.Extensions;
using Solo.Shared;
using Solo.Shared.Hubs;

namespace Solo.Api.Realtime;

/// <summary>
/// Broadcasts change events to board groups after successful saves. Called from
/// the API write path (not hub methods) so REST stays the single write path.
/// The sender is excluded via the connection id it supplies in the
/// <see cref="BoardHubConstants.ConnectionIdHeader"/> header.
/// </summary>
public interface IBoardNotifier
{
    Task BoardEntityChanged(Guid boardId, string entityType, Guid entityId,
        ChangeKind kind, params string[] changedFields);

    /// <summary>Project-scoped changes (labels, members, project meta) fan out to every board group.</summary>
    Task ProjectEntityChanged(Guid projectId, string entityType, Guid entityId,
        ChangeKind kind, params string[] changedFields);
}

/// <remarks>
/// Also the raise point for extension domain events. These call sites already
/// know the board/project scope and the changed fields — which a
/// ChangeTracker-derived event would not — so the SignalR broadcast and the
/// extension fan-out are the same moment, and neither can drift from the other.
/// Publishing is awaited but never allowed to fail the caller: the publisher
/// wraps every handler (see DomainEventPublisher).
/// </remarks>
public sealed class BoardNotifier(
    IHubContext<BoardHub, IBoardClient> hub,
    SoloDbContext db,
    CurrentUserAccessor currentUser,
    IHttpContextAccessor httpContextAccessor,
    IDomainEventPublisher events)
    : IBoardNotifier
{
    public async Task BoardEntityChanged(Guid boardId, string entityType, Guid entityId,
        ChangeKind kind, params string[] changedFields)
    {
        await Send(BoardHubConstants.BoardGroup(boardId), Event(entityType, entityId, kind, changedFields));
        await events.PublishAsync(new SoloDomainEvent(
            entityType, entityId, kind, changedFields, currentUser.UserId ?? Guid.Empty,
            ProjectId: null, BoardId: boardId));
    }

    public async Task ProjectEntityChanged(Guid projectId, string entityType, Guid entityId,
        ChangeKind kind, params string[] changedFields)
    {
        var boardIds = await db.Boards.AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .Select(b => b.Id)
            .ToListAsync();
        var change = Event(entityType, entityId, kind, changedFields);
        foreach (var boardId in boardIds)
        {
            await Send(BoardHubConstants.BoardGroup(boardId), change);
        }

        await events.PublishAsync(new SoloDomainEvent(
            entityType, entityId, kind, changedFields, currentUser.UserId ?? Guid.Empty,
            ProjectId: projectId, BoardId: null));
    }

    private EntityChangedEvent Event(string entityType, Guid entityId, ChangeKind kind, string[] changedFields) =>
        new(entityType, entityId, kind, changedFields, currentUser.UserId ?? Guid.Empty);

    private Task Send(string group, EntityChangedEvent change)
    {
        var senderConnectionId = httpContextAccessor.HttpContext?.Request.Headers[
            BoardHubConstants.ConnectionIdHeader].FirstOrDefault();
        var target = string.IsNullOrEmpty(senderConnectionId)
            ? hub.Clients.Group(group)
            : hub.Clients.GroupExcept(group, [senderConnectionId]);
        return target.EntityChanged(change);
    }
}
