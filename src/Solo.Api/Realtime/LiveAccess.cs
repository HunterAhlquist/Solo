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
    /// Re-checks every project the user's connections are listening to, takes
    /// them out of the ones the user can no longer read, and restates their role
    /// on the topics they keep. Called after anything that can lose someone read
    /// access, or change what they may write: leaving a project, or a change to
    /// their membership. (Super admin grants no read access, so losing it changes
    /// nothing here.)
    /// </summary>
    /// <remarks>
    /// The role matters because a topic remembers it for the messages its holder
    /// sends (contract 1.10). Without the restatement, someone demoted to Read
    /// would keep sending edits as a writer until their tab reconnected.
    /// </remarks>
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
            var membership = await db.ProjectMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
            if (AccessPolicy.Allows(membership?.Role, membership?.IsOwner ?? false, isSuperAdmin, ProjectAccessLevel.Read))
            {
                topics.UpdateRole(userId, projectId, membership?.Role ?? Solo.Shared.ProjectRole.Read,
                    membership?.IsOwner ?? false);
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
