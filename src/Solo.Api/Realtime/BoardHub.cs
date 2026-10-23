using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Data;
using Solo.Data.Domain;
using Solo.Extensibility;
using Solo.Extensions;
using Solo.Shared;
using Solo.Shared.Hubs;

namespace Solo.Api.Realtime;

/// <summary>
/// Live-editing hub: per-board groups for change broadcasts plus a lightweight
/// presence protocol, and the same two things for extensions on topics of their
/// own (see <see cref="Solo.Extensibility.ISoloLiveUpdates"/>). Change events are
/// broadcast from the REST write path via <see cref="IBoardNotifier"/>, keeping
/// REST as the single write path for core's own data — the hub itself only
/// manages membership and presence, and hands what clients send on an
/// extension's topics to that extension (<see cref="SendTopicMessage"/>).
/// </summary>
[Authorize]
public sealed class BoardHub(
    SoloDbContext db,
    UserProvisioningService provisioning,
    PresenceRegistry presence,
    TopicRegistry topics,
    SessionConnections sessions,
    LiveRateLimits rateLimits,
    LiveHandlerCatalog handlers,
    IHubContext<BoardHub, IBoardClient> hub,
    IServiceProvider services,
    ILogger<BoardHub> logger)
    : Hub<IBoardClient>, IBoardHubServer
{
    /// <summary>How long an extension's handler gets with one message before the sender is told it failed.</summary>
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(5);

    public override Task OnConnectedAsync()
    {
        sessions.Opened(Context);
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
        var (user, membership) = await RequireMembershipAsync(projectId);
        if (!topics.Subscribe(Context.ConnectionId, projectId, topic, user.Id, user.DisplayName ?? user.Email,
                membership?.Role ?? ProjectRole.Read, membership?.IsOwner ?? false))
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

    public async Task<LiveSendResult> SendTopicMessage(Guid projectId, string topic, string kind, string? data)
    {
        RequireTopic(topic);
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new HubException("A message needs a kind.");
        }

        if (kind.Length > BoardHubConstants.ClientMessageKindMaxLength ||
            data?.Length > BoardHubConstants.ClientMessageMaxLength)
        {
            return LiveSendResult.TooLarge;
        }

        // Subscribing is where read access was checked, and LiveAccess takes the
        // topic away when that access goes, so holding it is the access check.
        // A user topic has no project to answer for, and takes no messages.
        if (projectId == TopicRegistry.UserScope ||
            topics.Find(Context.ConnectionId, projectId, topic) is not { } subscription)
        {
            return LiveSendResult.NotSubscribed;
        }

        if (!rateLimits.TryAcquire(Context.ConnectionId))
        {
            return LiveSendResult.RateLimited;
        }

        if (handlers.Find(services, topic) is not { } found)
        {
            return LiveSendResult.NoHandler;
        }

        var (extensionId, handler) = found;

        var message = new LiveMessage(projectId, topic, kind, data, new LiveSender(
            subscription.UserId, subscription.DisplayName, subscription.Role, subscription.IsOwner));
        var reply = new LiveMessageReply(hub, Context.ConnectionId, projectId, topic, subscription.UserId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted);
        timeout.CancelAfter(HandlerTimeout);
        try
        {
            // WaitAsync as well as the token: a handler that ignores it still
            // doesn't get to hold the invocation.
            await handler.HandleAsync(message, reply, timeout.Token).AsTask().WaitAsync(HandlerTimeout);
            return LiveSendResult.Accepted;
        }
        catch (Exception e) when (e is not OperationCanceledException || !Context.ConnectionAborted.IsCancellationRequested)
        {
            logger.LogError(e, "Extension {Extension}'s live message handler failed on {Topic} ({Kind}).",
                extensionId, topic, kind);
            return LiveSendResult.Failed;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        sessions.Closed(Context.ConnectionId);
        rateLimits.Forget(Context.ConnectionId);
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
    private async Task<Solo.Data.Entities.User> RequireMemberAsync(Guid projectId) =>
        (await RequireMembershipAsync(projectId)).User;

    /// <summary>
    /// The caller and their membership, if they may read the project. The
    /// membership is what a topic remembers for the messages its holder sends.
    /// </summary>
    private async Task<(Solo.Data.Entities.User User, Solo.Data.Entities.ProjectMember? Membership)>
        RequireMembershipAsync(Guid projectId)
    {
        var user = await RequireUserAsync();
        var membership = await db.ProjectMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == user.Id, Context.ConnectionAborted);
        var allowed = AccessPolicy.Allows(
            membership?.Role, membership?.IsOwner ?? false, user.IsSuperAdmin, ProjectAccessLevel.Read);
        return allowed ? (user, membership) : throw new HubException("Not a member of this project.");
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
