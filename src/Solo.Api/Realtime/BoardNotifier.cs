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
/// Broadcasts saved REST API changes to board groups. Hub methods do not write,
/// and the originating SignalR connection is excluded when its id is available.
/// </summary>
public interface IBoardNotifier
{
    Task BoardEntityChanged(Guid boardId, string entityType, Guid entityId,
        ChangeKind kind, params string[] changedFields);

    /// <summary>Sends a project-scoped change to every board in that project.</summary>
    Task ProjectEntityChanged(Guid projectId, string entityType, Guid entityId,
        ChangeKind kind, params string[] changedFields);
}

/// <remarks>
/// Controllers provide scope and changed fields that the change tracker cannot
/// derive. SignalR and extension handlers receive the same change here, and the
/// publisher isolates handler failures.
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
        await Task.WhenAll(boardIds.Select(boardId =>
            Send(BoardHubConstants.BoardGroup(boardId), change)));

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
