using System.Collections.Concurrent;
using Solo.Mcp.Data;

namespace Solo.Mcp.Auth;

/// <summary>
/// What a token hash resolved to, remembered for a few minutes. This extension
/// authenticates ahead of core's rate limiter (see
/// <see cref="McpAuthenticationStartupFilter"/>), so without it every request
/// carrying an MCP token paid for a lookup before it could be throttled, the
/// bogus ones the limiter then turned away included.
/// </summary>
/// <remarks>
/// <para>Revocation has to bite at once, so every revocation bumps
/// <see cref="Generation"/> and entries from an older one are ignored.
/// <see cref="McpTokenRevocations.RevokeAsync"/> does the bump, and it is the
/// only way a token gets revoked, so no revoking endpoint can forget to. Static
/// for that reason: it is an extension method on a query, with no services to
/// hand. A bump only ever forgets more, so sharing one is never unsafe.</para>
///
/// <para>Misses are remembered too, briefly, and the table is capped: a stream
/// of distinct made-up tokens can't grow it without bound, it just stops
/// being remembered.</para>
/// </remarks>
public sealed class McpTokenCache
{
    /// <summary>The fields authentication needs: never the hash, never the owner's profile.</summary>
    public sealed record Lookup(
        Guid Id, string Issuer, string Subject, DateTimeOffset? RevokedAt, McpTokenRevocation? RevokedReason);

    private sealed record Entry(Lookup? Lookup, long Generation, DateTimeOffset ExpiresAt);

    private static readonly TimeSpan FoundFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MissingFor = TimeSpan.FromMinutes(1);
    private const int Capacity = 10_000;

    private static long s_generation;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public static long Generation => Interlocked.Read(ref s_generation);

    public static void Invalidate() => Interlocked.Increment(ref s_generation);

    /// <summary>True when the hash is remembered; <paramref name="lookup"/> is then null for "no such token".</summary>
    public bool TryGet(string hash, out Lookup? lookup)
    {
        if (_entries.TryGetValue(hash, out var entry) &&
            entry.Generation == Generation &&
            entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            lookup = entry.Lookup;
            return true;
        }

        lookup = null;
        return false;
    }

    /// <summary>
    /// Remembers a lookup. <paramref name="generation"/> is the one read before
    /// the query ran, so a revocation landing in between leaves it stale.
    /// </summary>
    public void Set(string hash, Lookup? lookup, long generation)
    {
        var now = DateTimeOffset.UtcNow;
        if (_entries.Count >= Capacity)
        {
            foreach (var (key, entry) in _entries)
            {
                if (entry.ExpiresAt <= now || entry.Generation != Generation)
                {
                    _entries.TryRemove(key, out _);
                }
            }

            if (_entries.Count >= Capacity)
            {
                _entries.Clear();
            }
        }

        _entries[hash] = new Entry(lookup, generation, now + (lookup is null ? MissingFor : FoundFor));
    }
}
