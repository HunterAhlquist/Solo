using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Data;
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

public sealed class BoardNotifier(
    IHubContext<BoardHub, IBoardClient> hub,
    SoloDbContext db,
    CurrentUserAccessor currentUser,
    IHttpContextAccessor httpContextAccessor)
    : IBoardNotifier
{
    public Task BoardEntityChanged(Guid boardId, string entityType, Guid entityId,
        ChangeKind kind, params string[] changedFields) =>
        Send(BoardHubConstants.BoardGroup(boardId), Event(entityType, entityId, kind, changedFields));

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
