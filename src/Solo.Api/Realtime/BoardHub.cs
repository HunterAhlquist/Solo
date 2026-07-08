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
/// presence protocol. Change events are broadcast from the REST write path via
/// <see cref="IBoardNotifier"/>, keeping REST as the single write path — the
/// hub itself only manages membership and presence.
/// </summary>
[Authorize]
public sealed class BoardHub(
    SoloDbContext db,
    UserProvisioningService provisioning,
    PresenceRegistry presence)
    : Hub<IBoardClient>, IBoardHubServer
{
    public async Task JoinBoard(Guid boardId)
    {
        var user = await provisioning.ResolveAsync(Context.User!, Context.ConnectionAborted)
                   ?? throw new HubException("Unknown user.");

        var projectId = await ProjectAccessHandler.ResolveProjectIdAsync(
                            db, ProjectAccessHandler.EntityRouteKind.Board, boardId, Context.ConnectionAborted)
                        ?? throw new HubException("Board not found.");

        // Same membership check that gates the REST API.
        var allowed = await ProjectAccessHandler.AllowsAsync(
            db, user.Id, user.IsSuperAdmin, projectId, ProjectAccessLevel.Read, Context.ConnectionAborted);
        if (!allowed)
        {
            throw new HubException("Not a member of this project.");
        }

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

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var boardId = presence.Leave(Context.ConnectionId);
        if (boardId is not null)
        {
            await Clients.Group(BoardHubConstants.BoardGroup(boardId.Value))
                .PresenceChanged(presence.Snapshot(boardId.Value));
        }

        await base.OnDisconnectedAsync(exception);
    }
}
