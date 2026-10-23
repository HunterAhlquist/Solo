using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Solo.Api.Realtime;

/// <summary>
/// One token bucket per hub connection for client-sent topic messages. Separate
/// from the <c>/api</c> limiter on purpose: a document being typed into sends a
/// steady stream, and sharing a bucket would let it starve the same tab's REST
/// calls.
/// </summary>
/// <remarks>
/// Per connection rather than per user, like the topic registry: a person with
/// two tabs open is two streams, and one runaway tab shouldn't silence the other.
/// </remarks>
public sealed class LiveRateLimits(int burst, int tokensPerSecond) : IDisposable
{
    private readonly ConcurrentDictionary<string, TokenBucketRateLimiter> _buckets = new();

    public bool TryAcquire(string connectionId)
    {
        var bucket = _buckets.GetOrAdd(connectionId, _ => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = burst,
            TokensPerPeriod = tokensPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
        using var lease = bucket.AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Forget(string connectionId)
    {
        if (_buckets.TryRemove(connectionId, out var bucket))
        {
            bucket.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var bucket in _buckets.Values)
        {
            bucket.Dispose();
        }

        _buckets.Clear();
    }
}
