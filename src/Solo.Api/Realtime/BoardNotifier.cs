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

    /// <summary>
    /// Nudges these users' own dashboards (<see cref="BoardHubConstants.AssignedTasksTopic"/>).
    /// The two methods above work out who is affected from what still exists
    /// after the save; this is for the people a save has just cut loose from the
    /// row — a deleted task's assignees, a sub-task's parent's, an assignee
    /// removed — whom nothing in the database leads back to any more.
    /// </summary>
    Task AssignedTasksChanged(IReadOnlyCollection<Guid> userIds, string entityType, Guid entityId, ChangeKind kind);
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
        await AssignedTasksChanged(await DashboardsOfAsync(boardId, entityType, entityId), entityType, entityId, kind);
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
        await AssignedTasksChanged(await MembersOfAsync(projectId), entityType, entityId, kind);

        await events.PublishAsync(new SoloDomainEvent(
            entityType, entityId, kind, changedFields, currentUser.UserId ?? Guid.Empty,
            ProjectId: projectId, BoardId: null));
    }

    public async Task AssignedTasksChanged(
        IReadOnlyCollection<Guid> userIds, string entityType, Guid entityId, ChangeKind kind)
    {
        if (userIds.Count == 0)
        {
            return;
        }

        var message = new TopicMessage(Guid.Empty, BoardHubConstants.AssignedTasksTopic,
            entityType, entityId.ToString(), currentUser.UserId ?? Guid.Empty);
        var sender = SenderConnectionId();
        await Task.WhenAll(userIds.Distinct().Select(userId =>
        {
            var group = BoardHubConstants.UserTopicGroup(userId, BoardHubConstants.AssignedTasksTopic);
            var target = sender is null ? hub.Clients.Group(group) : hub.Clients.GroupExcept(group, [sender]);
            return target.TopicMessage(message);
        }));
    }

    /// <summary>
    /// Whose dashboard a board-scoped change can show up on: a task's assignees
    /// for the task and its sub-tasks, the project's members for the board and
    /// its labels. Epics, groups and comments draw nothing there, so nobody.
    /// </summary>
    private async Task<IReadOnlyCollection<Guid>> DashboardsOfAsync(Guid boardId, string entityType, Guid entityId) =>
        entityType switch
        {
            EntityTypes.Task => await db.Assignments.AsNoTracking()
                .Where(a => a.TaskId == entityId).Select(a => a.UserId).ToListAsync(),
            EntityTypes.SubTask => await db.SubTasks.AsNoTracking()
                .Where(s => s.Id == entityId)
                .SelectMany(s => s.Task!.Assignments).Select(a => a.UserId).ToListAsync(),
            EntityTypes.Board or EntityTypes.Label => await db.ProjectMembers.AsNoTracking()
                .Where(m => m.UserId != null && m.Project!.Boards.Any(b => b.Id == boardId))
                .Select(m => m.UserId!.Value).ToListAsync(),
            _ => [],
        };

    private async Task<IReadOnlyCollection<Guid>> MembersOfAsync(Guid projectId) =>
        await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.UserId != null)
            .Select(m => m.UserId!.Value).ToListAsync();

    private EntityChangedEvent Event(string entityType, Guid entityId, ChangeKind kind, string[] changedFields) =>
        new(entityType, entityId, kind, changedFields, currentUser.UserId ?? Guid.Empty);

    private Task Send(string group, EntityChangedEvent change)
    {
        var senderConnectionId = SenderConnectionId();
        var target = senderConnectionId is null
            ? hub.Clients.Group(group)
            : hub.Clients.GroupExcept(group, [senderConnectionId]);
        return target.EntityChanged(change);
    }

    private string? SenderConnectionId()
    {
        var id = httpContextAccessor.HttpContext?.Request.Headers[BoardHubConstants.ConnectionIdHeader].FirstOrDefault();
        return string.IsNullOrEmpty(id) ? null : id;
    }
}
