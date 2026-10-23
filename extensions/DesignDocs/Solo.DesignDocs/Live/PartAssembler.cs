using System.Collections.Concurrent;
using System.Text;

namespace Solo.DesignDocs.Live;

/// <summary>
/// Puts <see cref="SyncProtocol.Part"/> messages back together, per sender and
/// document, so a long message is handled once it's whole.
/// </summary>
/// <remarks>
/// Held in memory, with an expiry and a cap per sender: parts that never
/// complete (a tab closed mid-send) are dropped rather than kept, and nobody
/// can hold more than <see cref="SyncProtocol.MaxMessageLength"/> in waiting
/// messages at once. Keyed by user rather than connection, since a handler
/// isn't told the connection; the sender's own message id keeps two tabs'
/// messages apart.
/// </remarks>
public sealed class PartAssembler(TimeProvider? time = null)
{
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(Guid User, Guid Document, string Id), Pending> _pending = new();

    /// <summary>
    /// Adds a part. Returns the whole message once its last part is in, and
    /// null until then, or when the part doesn't fit the rules.
    /// </summary>
    public (string Kind, string Data)? Add(Guid userId, Guid documentId, PartMessage part)
    {
        var now = _time.GetUtcNow();
        Sweep(now);

        var maxParts = (SyncProtocol.MaxMessageLength + SyncProtocol.ChunkLength - 1) / SyncProtocol.ChunkLength;
        if (part.N < 1 || part.N > maxParts || part.I < 0 || part.I >= part.N ||
            string.IsNullOrEmpty(part.Id) || part.Id.Length > 64 ||
            string.IsNullOrEmpty(part.K) || part.K == SyncProtocol.Part || part.D.Length > SyncProtocol.ChunkLength)
        {
            return null;
        }

        var key = (userId, documentId, part.Id);
        var pending = _pending.GetOrAdd(key, _ => new Pending(part.K, part.N, now + Expiry));
        lock (pending)
        {
            if (pending.Kind != part.K || pending.Slices.Length != part.N)
            {
                return null;
            }

            if (pending.Slices[part.I] is null)
            {
                if (WaitingFor(userId) + part.D.Length > SyncProtocol.MaxMessageLength)
                {
                    _pending.TryRemove(key, out _);
                    return null;
                }

                pending.Slices[part.I] = part.D;
                pending.Length += part.D.Length;
                pending.Received++;
            }

            if (pending.Received < part.N)
            {
                return null;
            }

            _pending.TryRemove(key, out _);
            var whole = new StringBuilder(pending.Length);
            foreach (var slice in pending.Slices)
            {
                whole.Append(slice);
            }

            return (pending.Kind, whole.ToString());
        }
    }

    /// <summary>Messages still waiting for parts. For tests.</summary>
    public int Count => _pending.Count;

    private long WaitingFor(Guid userId) =>
        _pending.Where(p => p.Key.User == userId).Sum(p => (long)p.Value.Length);

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, pending) in _pending)
        {
            if (pending.Expires <= now)
            {
                _pending.TryRemove(key, out _);
            }
        }
    }

    private sealed class Pending(string kind, int count, DateTimeOffset expires)
    {
        public string Kind { get; } = kind;
        public string?[] Slices { get; } = new string?[count];
        public DateTimeOffset Expires { get; } = expires;
        public int Received { get; set; }
        public int Length { get; set; }
    }
}
