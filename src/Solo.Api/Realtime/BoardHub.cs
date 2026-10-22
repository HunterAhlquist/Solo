using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Solo.Api.Auth;
using Microsoft.EntityFrameworkCore;
using Solo.Extensibility;
using Solo.Extensions;
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
/// REST as core's write path. Extensions can also opt into client messages on
/// their own topics; their handlers own validation and persistence.
/// </summary>
[Authorize]
public sealed class BoardHub(
    SoloDbContext db,
    UserProvisioningService provisioning,
    PresenceRegistry presence,
    TopicRegistry topics,
    SessionConnections sessions,
    LiveMessageLimits limits,
    LiveHandlerCatalog handlers,
    ExtensionState extensions,
    IServiceProvider services,
    IHubContext<BoardHub, IBoardClient> hub,
    ILogger<BoardHub> logger)
    : Hub<IBoardClient>, IBoardHubServer
{
    public override Task OnConnectedAsync()
    {
        sessions.Opened(Context);
        limits.Open(Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public async Task JoinBoard(Guid boardId)
    {
        var projectId = await ProjectAccessHandler.ResolveProjectIdAsync(
                            db, ProjectAccessHandler.EntityRouteKind.Board, boardId, Context.ConnectionAborted)
                        ?? throw new HubException("Board not found.");
        var user = await RequireMemberAsync(projectId);

        await Groups.AddToGroupAsync(Context.ConnectionId, BoardHubConstants.BoardGroup(boardId));
        presence.Join(Context.ConnectionId, projectId, boardId, user.Id, user.DisplayName ?? user.Email);
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
        var user = await RequireUserAsync();
        var membership = await db.ProjectMembers.AsNoTracking().SingleOrDefaultAsync(
            m => m.ProjectId == projectId && m.UserId == user.Id, Context.ConnectionAborted);
        if (membership is null) throw new HubException("Not a member of this project.");
        if (!topics.Subscribe(Context.ConnectionId, projectId, topic, user.Id, user.DisplayName ?? user.Email,
                membership.Role, membership.IsOwner))
        {
            throw new HubException($"A connection can hold at most {BoardHubConstants.TopicsPerConnection} topics.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, BoardHubConstants.TopicGroup(projectId, topic));
        // Whoever was already there, so a page arriving late doesn't start empty.
        await Clients.Caller.TopicPresenceChanged(topics.Snapshot(projectId, topic));
    }

    public async Task<LiveSendResult> SendTopicMessage(Guid projectId, string topic, string kind, string? data)
    {
        RequireTopic(topic);
        if (string.IsNullOrEmpty(kind) || kind.Length > 64) return LiveSendResult.Failed;
        if (data?.Length > BoardHubConstants.ClientMessageMaxLength) return LiveSendResult.TooLarge;
        if (topics.Sender(Context.ConnectionId, projectId, topic) is not { } sender)
            return LiveSendResult.NotSubscribed;
        if (!limits.TryAcquire(Context.ConnectionId)) return LiveSendResult.RateLimited;

        var extensionId = topic.Split(':', 2)[0];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (handlers.Find(topic, services, extensions) is not { } entry) return LiveSendResult.NoHandler;
            var message = new LiveMessage(projectId, topic, kind, data, sender);
            await entry.Handler.HandleAsync(message,
                new LiveMessageReply(hub, Context.ConnectionId, message, timeout.Token), timeout.Token)
                .AsTask().WaitAsync(timeout.Token);
            return LiveSendResult.Accepted;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Extension {ExtensionId} failed to handle a live message.", extensionId);
            return LiveSendResult.Failed;
        }
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
        if (projectId == TopicRegistry.UserScope)
        {
            throw new HubException("A user topic has no presence.");
        }

        // Subscribing is where access was checked; presence rides on it.
        if (!topics.SetPresence(Context.ConnectionId, projectId, topic, mode))
        {
            throw new HubException("Subscribe to the topic first.");
        }

        await Clients.Group(BoardHubConstants.TopicGroup(projectId, topic))
            .TopicPresenceChanged(topics.Snapshot(projectId, topic));
    }

    public async Task SubscribeUserTopic(string topic)
    {
        RequireTopic(topic);
        // The group is named for whoever is signed in on this connection, so
        // there is no one else's to ask for and nothing further to check.
        var user = await RequireUserAsync();
        if (!topics.Subscribe(Context.ConnectionId, TopicRegistry.UserScope, topic, user.Id, user.DisplayName ?? user.Email))
        {
            throw new HubException($"A connection can hold at most {BoardHubConstants.TopicsPerConnection} topics.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, BoardHubConstants.UserTopicGroup(user.Id, topic));
    }

    public async Task UnsubscribeUserTopic(string topic)
    {
        RequireTopic(topic);
        var user = await RequireUserAsync();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, BoardHubConstants.UserTopicGroup(user.Id, topic));
        topics.Unsubscribe(Context.ConnectionId, TopicRegistry.UserScope, topic);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        sessions.Closed(Context.ConnectionId);
        limits.Close(Context.ConnectionId);
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
        var user = await RequireUserAsync();

        var allowed = await ProjectAccessHandler.AllowsAsync(
            db, user.Id, user.IsSuperAdmin, projectId, ProjectAccessLevel.Read, Context.ConnectionAborted);
        return allowed ? user : throw new HubException("Not a member of this project.");
    }

    private async Task<Solo.Data.Entities.User> RequireUserAsync() =>
        await provisioning.ResolveAsync(Context.User!, Context.ConnectionAborted)
        ?? throw new HubException("Unknown user.");

    private static void RequireTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic) || topic.Length > BoardHubConstants.TopicMaxLength)
        {
            throw new HubException($"A topic is 1–{BoardHubConstants.TopicMaxLength} characters.");
        }
    }
}
