using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Data;
using Solo.Data.Domain;
using Solo.Shared.Hubs;

namespace Solo.Api.Realtime;

/// <summary>
/// Keeps live connections in step with access that changed after they were
/// granted. The hub checks access once, when a connection joins a board or
/// subscribes to a topic; without this, a member removed from a project kept
/// hearing its changes, and who was viewing what, until their tab disconnected.
/// </summary>
public sealed class LiveAccess(
    SoloDbContext db,
    IHubContext<BoardHub, IBoardClient> hub,
    PresenceRegistry presence,
    TopicRegistry topics,
    SessionConnections sessions,
    HubTickets tickets)
{
    /// <summary>
    /// Re-checks every project the user's connections are listening to, and
    /// takes them out of the ones the user can no longer read. Called after
    /// anything that can lose someone read access, which today is leaving a
    /// project. (Super admin grants no read access, so losing it changes nothing here.)
    /// </summary>
    public async Task RevalidateAsync(Guid userId, CancellationToken ct)
    {
        var boards = presence.JoinedBy(userId);
        // A user's own topics follow the person, not a project, so leaving one
        // takes nothing away from them.
        var held = topics.HeldBy(userId).Where(t => t.ProjectId != TopicRegistry.UserScope).ToList();
        if (boards.Count == 0 && held.Count == 0)
        {
            return;
        }

        var isSuperAdmin = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.IsSuperAdmin).FirstOrDefaultAsync(ct);
        foreach (var projectId in boards.Select(b => b.ProjectId).Concat(held.Select(t => t.ProjectId)).Distinct())
        {
            if (await ProjectAccessHandler.AllowsAsync(db, userId, isSuperAdmin, projectId, ProjectAccessLevel.Read, ct))
            {
                continue;
            }

            foreach (var (connectionId, _, boardId) in boards.Where(b => b.ProjectId == projectId))
            {
                var group = BoardHubConstants.BoardGroup(boardId);
                await hub.Groups.RemoveFromGroupAsync(connectionId, group, ct);
                if (presence.Leave(connectionId, boardId))
                {
                    // Everyone still there stops seeing them, as they stop seeing everyone.
                    await hub.Clients.Group(group).PresenceChanged(presence.Snapshot(boardId));
                }
            }

            foreach (var (connectionId, _, topic) in held.Where(t => t.ProjectId == projectId))
            {
                var group = BoardHubConstants.TopicGroup(projectId, topic);
                await hub.Groups.RemoveFromGroupAsync(connectionId, group, ct);
                if (topics.Unsubscribe(connectionId, projectId, topic))
                {
                    await hub.Clients.Group(group).TopicPresenceChanged(topics.Snapshot(projectId, topic));
                }
            }
        }
    }

    /// <summary>
    /// Closes the hub connections these sessions opened, once they are revoked,
    /// and voids the tickets they minted that haven't connected yet. The app
    /// reconnects if it can, which a revoked session can't.
    /// </summary>
    public void EndSessions(IReadOnlyCollection<Guid> sessionIds)
    {
        if (sessionIds.Count == 0)
        {
            return;
        }

        tickets.Revoke(sessionIds);
        sessions.Abort(sessionIds);
    }
}
