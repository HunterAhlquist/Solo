namespace Solo.Client.Realtime;

/// <summary>
/// Carries the live hub connection id from <see cref="BoardHubClient"/> to the
/// HTTP layer, which stamps it on every request as
/// <c>X-Connection-Id</c>. The server uses it to exclude this client from its
/// own change broadcasts — no echo, no double-apply.
/// </summary>
public sealed class ConnectionIdAccessor
{
    public string? ConnectionId { get; set; }
}
