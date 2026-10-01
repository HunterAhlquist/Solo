using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Solo.Api.Auth;
using Solo.Data;
using Solo.Data.Domain;
using Solo.Shared;
using Solo.Shared.Hubs;

namespace Solo.Api.Realtime;

/// <summary>
/// Live-editing hub: per-board groups for change broadcasts plus a lightweight
/// presence protocol, and the same two things for extensions on topics of their
/// own (see <see cref="Solo.Extensibility.ISoloLiveUpdates"/>). Change events are
/// broadcast from the REST write path via <see cref="IBoardNotifier"/>, keeping
/// REST as the single write path — the hub itself only manages membership and
/// presence.
/// </summary>
[Authorize]
public sealed class BoardHub(
    SoloDbContext db,
    UserProvisioningService provisioning,
    PresenceRegistry presence,
    TopicRegistry topics)
    : Hub<IBoardClient>, IBoardHubServer
{
    public async Task JoinBoard(Guid boardId)
    {
        var projectId = await ProjectAccessHandler.ResolveProjectIdAsync(
                            db, ProjectAccessHandler.EntityRouteKind.Board, boardId, Context.ConnectionAborted)
                        ?? throw new HubException("Board not found.");
        var user = await RequireMemberAsync(projectId);

        await Groups.AddToGroupAsync(Context.ConnectionId, BoardHubConstants.BoardGroup(boardId));
        presence.Join(Context.ConnectionId, boardId, user.Id, user.DisplayName ?? user.Email);
        await Clients.Group(BoardHubConstants.BoardGroup(boardId))
            .PresenceChanged(presence.Snapshot(boardId));
    }

    public async Task LeaveBoard(Guid boardId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, BoardHubConstants.BoardGroup(boardId));
        presence.Leave(Context.ConnectionId);
        await Clients.Group(BoardHubConstants.BoardGroup(boardId))
            .PresenceChanged(presence.Snapshot(boardId));
    }

    public async Task SetPresence(string entityType, Guid entityId, PresenceMode mode)
    {
        var boardId = presence.SetPresence(Context.ConnectionId, entityType, entityId, mode);
        if (boardId is not null)
        {
            await Clients.Group(BoardHubConstants.BoardGroup(boardId.Value))
                .PresenceChanged(presence.Snapshot(boardId.Value));
        }
    }

    public async Task SubscribeTopic(Guid projectId, string topic)
    {
        RequireTopic(topic);
        var user = await RequireMemberAsync(projectId);
        if (!topics.Subscribe(Context.ConnectionId, projectId, topic, user.Id, user.DisplayName ?? user.Email))
        {
            throw new HubException($"A connection can hold at most {BoardHubConstants.TopicsPerConnection} topics.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, BoardHubConstants.TopicGroup(projectId, topic));
        // Whoever was already there, so a page arriving late doesn't start empty.
        await Clients.Caller.TopicPresenceChanged(topics.Snapshot(projectId, topic));
    }

    public async Task UnsubscribeTopic(Guid projectId, string topic)
    {
        RequireTopic(topic);
        var group = BoardHubConstants.TopicGroup(projectId, topic);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
        if (topics.Unsubscribe(Context.ConnectionId, projectId, topic))
        {
            await Clients.Group(group).TopicPresenceChanged(topics.Snapshot(projectId, topic));
        }
    }

    public async Task SetTopicPresence(Guid projectId, string topic, PresenceMode mode)
    {
        RequireTopic(topic);
        // Subscribing is where access was checked; presence rides on it.
        if (!topics.SetPresence(Context.ConnectionId, projectId, topic, mode))
        {
            throw new HubException("Subscribe to the topic first.");
        }

        await Clients.Group(BoardHubConstants.TopicGroup(projectId, topic))
            .TopicPresenceChanged(topics.Snapshot(projectId, topic));
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var boardId = presence.Leave(Context.ConnectionId);
        if (boardId is not null)
        {
            await Clients.Group(BoardHubConstants.BoardGroup(boardId.Value))
                .PresenceChanged(presence.Snapshot(boardId.Value));
        }

        foreach (var (projectId, topic) in topics.Leave(Context.ConnectionId))
        {
            await Clients.Group(BoardHubConstants.TopicGroup(projectId, topic))
                .TopicPresenceChanged(topics.Snapshot(projectId, topic));
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>The caller, if they may read the project — the same check that gates the REST API.</summary>
    private async Task<Solo.Data.Entities.User> RequireMemberAsync(Guid projectId)
    {
        var user = await provisioning.ResolveAsync(Context.User!, Context.ConnectionAborted)
                   ?? throw new HubException("Unknown user.");

        var allowed = await ProjectAccessHandler.AllowsAsync(
            db, user.Id, user.IsSuperAdmin, projectId, ProjectAccessLevel.Read, Context.ConnectionAborted);
        return allowed ? user : throw new HubException("Not a member of this project.");
    }

    private static void RequireTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic) || topic.Length > BoardHubConstants.TopicMaxLength)
        {
            throw new HubException($"A topic is 1–{BoardHubConstants.TopicMaxLength} characters.");
        }
    }
}
