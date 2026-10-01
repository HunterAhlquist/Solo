using System.Collections.Concurrent;
using Solo.Shared;
using Solo.Shared.Hubs;

namespace Solo.Api.Realtime;

/// <summary>
/// Which extension topics each connection holds, and its presence on each. In
/// memory for the same reason as <see cref="PresenceRegistry"/>: one instance.
/// </summary>
/// <remarks>
/// Subscribing and being present are separate: a sidebar list subscribes to
/// hear about changes without announcing anyone, while a page also reports its
/// reader. A connection is a tab, so one person with two tabs open is two
/// entries here and one in a snapshot, at the stronger of the two modes.
/// </remarks>
public sealed class TopicRegistry
{
    private sealed record Subscription(Guid UserId, string? DisplayName, PresenceMode Mode);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(Guid ProjectId, string Topic), Subscription>>
        _connections = new();

    /// <summary>False when the connection already holds as many topics as it may.</summary>
    public bool Subscribe(string connectionId, Guid projectId, string topic, Guid userId, string? displayName)
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

        topics[(projectId, topic)] = new Subscription(userId, displayName, PresenceMode.None);
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

        topics[(projectId, topic)] = subscription with { Mode = mode };
        return true;
    }

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
