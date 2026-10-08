using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Solo.Shared.Hubs;

namespace Solo.Api.Auth;

/// <summary>
/// Short-lived credentials for the SignalR hub's URL. A browser can't put an
/// Authorization header on a WebSocket, so the hub's credential travels in the
/// query string, and every proxy and CDN on the way writes the query string to
/// its access log. Sending the real credential there logged the desktop app's
/// 30-day session, or the web app's hour-long ID token, in plain text. A
/// ticket goes there instead: minted over an authenticated POST just before
/// connecting, good for <see cref="Lifetime"/>, and accepted only on the hub.
/// </summary>
/// <remarks>
/// <para>In a URL a ticket works once, so a logged one is already spent. In the
/// Authorization header it can be reused until it expires, because the client
/// sends it there for the negotiate request and every long-polling request,
/// and a header is not logged.</para>
///
/// <para>In memory, because the deployment is one instance (as for
/// <c>PresenceRegistry</c>), and a ticket outlives a restart by seconds at most.</para>
/// </remarks>
public sealed class HubTickets(TimeProvider time)
{
    public const string Prefix = "soloht_";

    /// <summary>Long enough to negotiate and connect, short enough that a log line is worthless.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Who a ticket stands for, taken from the stored user rather than from the
    /// minting request's claims, the way a session principal is built.
    /// </summary>
    public sealed record Holder(string Issuer, string Subject, string Email, string? DisplayName, Guid? SessionId);

    private sealed record Ticket(Holder Holder, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Ticket> _tickets = new();

    public string Issue(Holder holder)
    {
        var now = time.GetUtcNow();
        // Swept here rather than on a timer: issuing is the only thing that
        // grows the table, and the API rate limit bounds how fast it can.
        foreach (var (hash, ticket) in _tickets)
        {
            if (ticket.ExpiresAt <= now)
            {
                _tickets.TryRemove(hash, out _);
            }
        }

        var token = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _tickets[SessionTokens.Hash(token)] = new Ticket(holder, now + Lifetime);
        return token;
    }

    /// <summary>The ticket's holder, or null when it is unknown, expired or already spent in a URL.</summary>
    public Holder? Redeem(string token, bool fromUrl)
    {
        var hash = SessionTokens.Hash(token);
        var found = fromUrl
            ? _tickets.TryRemove(hash, out var ticket)
            : _tickets.TryGetValue(hash, out ticket);
        return found && ticket!.ExpiresAt > time.GetUtcNow() ? ticket.Holder : null;
    }

    /// <summary>Voids the unspent tickets these sessions minted, once they are revoked.</summary>
    public void Revoke(IReadOnlyCollection<Guid> sessionIds)
    {
        foreach (var (hash, ticket) in _tickets)
        {
            if (ticket.Holder.SessionId is { } sessionId && sessionIds.Contains(sessionId))
            {
                _tickets.TryRemove(hash, out _);
            }
        }
    }

    /// <summary>
    /// A ticket on the hub path, from the Authorization header or the
    /// <c>access_token</c> query parameter, and which of the two it came from.
    /// Anywhere else a ticket is not a credential at all.
    /// </summary>
    public static (string Token, bool FromUrl)? FromRequest(HttpRequest request)
    {
        if (!request.Path.StartsWithSegments(BoardHubConstants.Path))
        {
            return null;
        }

        string header = request.Headers.Authorization.ToString();
        if (header.StartsWith($"{JwtBearerDefaults.AuthenticationScheme} {Prefix}", StringComparison.OrdinalIgnoreCase))
        {
            return (header[(JwtBearerDefaults.AuthenticationScheme.Length + 1)..], false);
        }

        return request.Query["access_token"].ToString() is { } query && query.StartsWith(Prefix, StringComparison.Ordinal)
            ? (query, true)
            : null;
    }
}
