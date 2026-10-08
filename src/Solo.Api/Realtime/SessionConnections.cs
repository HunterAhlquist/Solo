using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Solo.Api.Auth;

namespace Solo.Api.Realtime;

/// <summary>
/// Hub connections opened with a Solo session (the desktop app), by session. A
/// connection proves its credential once, when it opens, so revoking the
/// session alone left the app that held it listening until it next dropped.
/// In memory for the same reason as <see cref="PresenceRegistry"/>: one instance.
/// </summary>
public sealed class SessionConnections
{
    private readonly ConcurrentDictionary<string, (Guid SessionId, HubCallerContext Context)> _connections = new();

    public void Opened(HubCallerContext context)
    {
        if (Guid.TryParse(context.User?.FindFirst(AuthConstants.SessionIdClaim)?.Value, out var sessionId))
        {
            _connections[context.ConnectionId] = (sessionId, context);
        }
    }

    public void Closed(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>Drops every connection opened with one of these sessions.</summary>
    public void Abort(IReadOnlyCollection<Guid> sessionIds)
    {
        foreach (var (_, (sessionId, context)) in _connections)
        {
            if (sessionIds.Contains(sessionId))
            {
                context.Abort();
            }
        }
    }
}
