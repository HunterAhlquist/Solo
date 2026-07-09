using Solo.Client.Realtime;
using Solo.Shared;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests;

/// <summary>Drives the change feed and presence without opening a WebSocket.</summary>
internal sealed class FakeBoardHub : IBoardHubClient
{
    public event Func<EntityChangedEvent, Task>? EntityChanged;

    public event Func<BoardPresenceSnapshot, Task>? PresenceChanged;

    public event Func<Task>? ConnectionStateChanged;

    public bool IsConnected { get; set; } = true;

    public bool IsActive { get; set; }

    public List<Guid> Joined { get; } = [];

    public List<Guid> Left { get; } = [];

    public List<(string EntityType, Guid EntityId, PresenceMode Mode)> PresenceReports { get; } = [];

    public Task JoinBoard(Guid boardId)
    {
        Joined.Add(boardId);
        return Task.CompletedTask;
    }

    public Task LeaveBoard(Guid boardId)
    {
        Left.Add(boardId);
        return Task.CompletedTask;
    }

    public Task SetPresence(string entityType, Guid entityId, PresenceMode mode)
    {
        PresenceReports.Add((entityType, entityId, mode));
        return Task.CompletedTask;
    }

    public Task RaiseEntityChanged(EntityChangedEvent change) =>
        EntityChanged?.Invoke(change) ?? Task.CompletedTask;

    public Task RaisePresence(BoardPresenceSnapshot snapshot) =>
        PresenceChanged?.Invoke(snapshot) ?? Task.CompletedTask;

    public Task RaiseConnectionStateChanged() => ConnectionStateChanged?.Invoke() ?? Task.CompletedTask;
}
