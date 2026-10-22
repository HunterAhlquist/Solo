using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Solo.Api.Realtime;

/// <summary>Live traffic has its own budget, so typing cannot starve the REST API.</summary>
public sealed class LiveMessageLimits(IConfiguration configuration) : IDisposable
{
    private readonly ConcurrentDictionary<string, TokenBucketRateLimiter> _connections = new();
    private readonly int _burst = Math.Max(1, configuration.GetValue("RateLimiting:Live:Burst", 200));
    private readonly int _perSecond = Math.Max(1, configuration.GetValue("RateLimiting:Live:TokensPerSecond", 50));

    public void Open(string connectionId) => _connections.TryAdd(connectionId, new(new()
    {
        TokenLimit = _burst,
        TokensPerPeriod = _perSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        AutoReplenishment = true,
        QueueLimit = 0,
    }));

    public bool TryAcquire(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var limiter)) return false;
        using var lease = limiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Close(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var limiter)) limiter.Dispose();
    }

    public void Dispose()
    {
        foreach (var id in _connections.Keys) Close(id);
    }
}
