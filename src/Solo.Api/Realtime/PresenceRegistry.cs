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
    private sealed record Connection(
        Guid ProjectId, Guid BoardId, Guid UserId, string? DisplayName, PresenceEntry? Presence);

    private readonly ConcurrentDictionary<string, Connection> _connections = new();

    public void Join(string connectionId, Guid projectId, Guid boardId, Guid userId, string? displayName) =>
        _connections[connectionId] = new Connection(projectId, boardId, userId, displayName, null);

    /// <summary>Returns the board the connection was in, if any.</summary>
    public Guid? Leave(string connectionId) =>
        _connections.TryRemove(connectionId, out var connection) ? connection.BoardId : null;

    /// <summary>
    /// Takes the connection off <paramref name="boardId"/> only if it is still
    /// there, so an eviction can't knock it off a board it has since moved to.
    /// Retried because a presence report can replace the entry in between.
    /// </summary>
    public bool Leave(string connectionId, Guid boardId)
    {
        while (_connections.TryGetValue(connectionId, out var connection) && connection.BoardId == boardId)
        {
            if (_connections.TryRemove(new KeyValuePair<string, Connection>(connectionId, connection)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The boards a user's connections have joined, for re-checking their access.</summary>
    public IReadOnlyList<(string ConnectionId, Guid ProjectId, Guid BoardId)> JoinedBy(Guid userId) =>
        _connections
            .Where(c => c.Value.UserId == userId)
            .Select(c => (c.Key, c.Value.ProjectId, c.Value.BoardId))
            .ToList();

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
