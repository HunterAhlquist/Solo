using System.Collections.Concurrent;
using Solo.Shared;
using Solo.Shared.Hubs;

namespace Solo.Api.Realtime;

/// <summary>
/// In-memory presence tracking keyed by SignalR connection id. In-memory is
/// fine because deployment is single-instance; a Redis backplane is the
/// documented scale-out path if ever needed.
/// </summary>
public sealed class PresenceRegistry
{
    private sealed record Connection(Guid BoardId, Guid UserId, string? DisplayName, PresenceEntry? Presence);

    private readonly ConcurrentDictionary<string, Connection> _connections = new();

    public void Join(string connectionId, Guid boardId, Guid userId, string? displayName) =>
        _connections[connectionId] = new Connection(boardId, userId, displayName, null);

    /// <summary>Returns the board the connection was in, if any.</summary>
    public Guid? Leave(string connectionId) =>
        _connections.TryRemove(connectionId, out var connection) ? connection.BoardId : null;

    public Guid? SetPresence(string connectionId, string entityType, Guid entityId, PresenceMode mode)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return null;
        }

        var presence = mode == PresenceMode.None
            ? null
            : new PresenceEntry(connection.UserId, connection.DisplayName, entityType, entityId, mode);
        _connections[connectionId] = connection with { Presence = presence };
        return connection.BoardId;
    }

    public BoardPresenceSnapshot Snapshot(Guid boardId) =>
        new(boardId, _connections.Values
            .Where(c => c.BoardId == boardId && c.Presence is not null)
            .Select(c => c.Presence!)
            .ToList());
}
