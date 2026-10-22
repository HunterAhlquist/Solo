using System.Collections.Concurrent;
using Solo.Shared;
using Solo.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Api.Realtime;

/// <summary>
/// Which extension topics each connection holds, and its presence on each. In
/// memory for the same reason as <see cref="PresenceRegistry"/>: one instance.
/// </summary>
/// <remarks>
/// <para>Subscribing and being present are separate: a sidebar list subscribes to
/// hear about changes without announcing anyone, while a page also reports its
/// reader. A connection is a tab, so one person with two tabs open is two
/// entries here and one in a snapshot, at the stronger of the two modes.</para>
///
/// <para>A user's own topics are held here too, under <see cref="UserScope"/> in
/// place of a project, so they count toward the same per-connection limit and
/// go when the connection does. Nobody reports presence on one.</para>
/// </remarks>
public sealed class TopicRegistry
{
    /// <summary>The "project" a user topic is held under. No project has an empty id.</summary>
    public static readonly Guid UserScope = Guid.Empty;

    private sealed record Subscription(Guid UserId, string? DisplayName, PresenceMode Mode, ProjectRole Role, bool IsOwner);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(Guid ProjectId, string Topic), Subscription>>
        _connections = new();

    /// <summary>False when the connection already holds as many topics as it may.</summary>
    public bool Subscribe(string connectionId, Guid projectId, string topic, Guid userId, string? displayName,
        ProjectRole role = ProjectRole.Read, bool isOwner = false)
    {
        var topics = _connections.GetOrAdd(connectionId, _ => new());
        if (topics.ContainsKey((projectId, topic)))
        {
            return true;
        }

        if (topics.Count >= BoardHubConstants.TopicsPerConnection)
        {
            return false;
        }

        topics[(projectId, topic)] = new Subscription(userId, displayName, PresenceMode.None, role, isOwner);
        return true;
    }

    /// <summary>True when the connection was present there, so the topic's snapshot changed.</summary>
    public bool Unsubscribe(string connectionId, Guid projectId, string topic) =>
        _connections.TryGetValue(connectionId, out var topics) &&
        topics.TryRemove((projectId, topic), out var removed) &&
        removed.Mode != PresenceMode.None;

    /// <summary>False when the connection isn't subscribed to the topic.</summary>
    public bool SetPresence(string connectionId, Guid projectId, string topic, PresenceMode mode)
    {
        if (!_connections.TryGetValue(connectionId, out var topics) ||
            !topics.TryGetValue((projectId, topic), out var subscription))
        {
            return false;
        }

        // A presence report must not overwrite a concurrent role refresh.
        while (!topics.TryUpdate((projectId, topic), subscription with { Mode = mode }, subscription))
            if (!topics.TryGetValue((projectId, topic), out subscription)) return false;
        return true;
    }

    public LiveSender? Sender(string connectionId, Guid projectId, string topic) =>
        projectId != UserScope && _connections.TryGetValue(connectionId, out var held) &&
        held.TryGetValue((projectId, topic), out var s)
            ? new(s.UserId, s.DisplayName, s.Role, s.IsOwner) { ConnectionId = connectionId } : null;

    public void RefreshRole(Guid userId, Guid projectId, ProjectRole role, bool isOwner)
    {
        foreach (var held in _connections.Values)
            foreach (var (key, s) in held.Where(t => t.Key.ProjectId == projectId && t.Value.UserId == userId))
            {
                var current = s;
                while (!held.TryUpdate(key, current with { Role = role, IsOwner = isOwner }, current))
                    if (!held.TryGetValue(key, out current)) break;
            }
    }

    /// <summary>The topics a user's connections hold, for re-checking their access.</summary>
    public IReadOnlyList<(string ConnectionId, Guid ProjectId, string Topic)> HeldBy(Guid userId) =>
        _connections
            .SelectMany(connection => connection.Value
                .Where(t => t.Value.UserId == userId)
                .Select(t => (connection.Key, t.Key.ProjectId, t.Key.Topic)))
            .ToList();

    /// <summary>Forgets the connection; returns the topics it was present on, whose snapshots changed.</summary>
    public IReadOnlyList<(Guid ProjectId, string Topic)> Leave(string connectionId) =>
        _connections.TryRemove(connectionId, out var topics)
            ? topics.Where(t => t.Value.Mode != PresenceMode.None).Select(t => t.Key).ToList()
            : [];

    public TopicPresenceSnapshot Snapshot(Guid projectId, string topic) =>
        new(projectId, topic, _connections.Values
            .Select(topics => topics.TryGetValue((projectId, topic), out var s) ? s : null)
            .Where(s => s is { Mode: not PresenceMode.None })
            .GroupBy(s => s!.UserId)
            .Select(g => g.OrderByDescending(s => s!.Mode).First()!)
            .Select(s => new TopicPresenceEntry(s.UserId, s.DisplayName, s.Mode))
            .ToList());
}
