using System.Collections.Concurrent;

namespace Solo.DesignDocs.Live;

/// <summary>
/// Who is turning an imported note into a live document right now.
/// </summary>
/// <remarks>
/// The server can't build a Yjs document from markdown, so the first writer to
/// open an imported note does it and sends the result as an ordinary update.
/// Two writers doing it at once would each insert the whole text, and Yjs
/// would keep both copies, so one writer at a time holds a lease and only its
/// update is stored as the seed. A lease runs out on its own, so a tab closed
/// mid-seed only delays the next writer.
/// </remarks>
public sealed class SeedLeases(TimeProvider? time = null)
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, Lease> _leases = new();

    /// <summary>
    /// A new lease for <paramref name="userId"/>, or null while someone else's
    /// is running. The same user asking again (a second tab, a reload) gets a
    /// fresh lease and the old one stops counting.
    /// </summary>
    public string? TryTake(Guid documentId, Guid userId)
    {
        var now = _time.GetUtcNow();
        var lease = new Lease(Guid.NewGuid().ToString("N"), userId, now + Duration);
        while (true)
        {
            if (_leases.TryGetValue(documentId, out var current))
            {
                if (current.UserId != userId && current.Expires > now)
                {
                    return null;
                }

                if (_leases.TryUpdate(documentId, lease, current))
                {
                    return lease.Id;
                }
            }
            else if (_leases.TryAdd(documentId, lease))
            {
                return lease.Id;
            }
        }
    }

    public bool IsValid(Guid documentId, string leaseId) =>
        _leases.TryGetValue(documentId, out var lease) &&
        lease.Id == leaseId &&
        lease.Expires > _time.GetUtcNow();

    public void Release(Guid documentId) => _leases.TryRemove(documentId, out _);

    private sealed record Lease(string Id, Guid UserId, DateTimeOffset Expires);
}
