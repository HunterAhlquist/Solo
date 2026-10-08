using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Solo.Mcp.Data;

namespace Solo.Mcp.Auth;

/// <summary>
/// Counts accepted requests per token and hour in memory, and writes them out
/// in batches. Writing on every request cost each one two statements before
/// core's rate limiter had a say.
/// </summary>
/// <remarks>
/// Written out every minute by <see cref="McpUsageFlusher"/>, on shutdown, and
/// before anything reads usage, so the admin's graph and a token's last-used
/// time are never behind. A crash loses at most the last minute's counts,
/// which for a usage graph is an acceptable price.
/// </remarks>
public sealed class McpUsageCounter
{
    private readonly ConcurrentDictionary<(Guid TokenId, DateTimeOffset Hour), int> _counts = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastUsed = new();
    private readonly SemaphoreSlim _flushing = new(1, 1);

    public void Record(Guid tokenId)
    {
        var now = DateTimeOffset.UtcNow;
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        _counts.AddOrUpdate((tokenId, hour), 1, (_, count) => count + 1);
        _lastUsed.AddOrUpdate(tokenId, now, (_, previous) => now > previous ? now : previous);
    }

    /// <summary>
    /// Adds what was counted to the stored buckets. Anything whose write fails
    /// goes back in, to be retried with the next flush.
    /// </summary>
    public async Task FlushAsync(McpDbContext db, CancellationToken ct)
    {
        await _flushing.WaitAsync(ct);
        try
        {
            foreach (var key in _counts.Keys)
            {
                if (!_counts.TryRemove(key, out var count))
                {
                    continue;
                }

                try
                {
                    await db.Database.ExecuteSqlRawAsync(AddUsageSql, [key.TokenId, key.Hour, count], ct);
                }
                catch
                {
                    _counts.AddOrUpdate(key, count, (_, later) => later + count);
                    throw;
                }
            }

            foreach (var tokenId in _lastUsed.Keys)
            {
                if (!_lastUsed.TryRemove(tokenId, out var lastUsed))
                {
                    continue;
                }

                try
                {
                    await db.Database.ExecuteSqlRawAsync(LastUsedSql, [tokenId, lastUsed], ct);
                }
                catch
                {
                    _lastUsed.AddOrUpdate(tokenId, lastUsed, (_, later) => later > lastUsed ? later : lastUsed);
                    throw;
                }
            }
        }
        finally
        {
            _flushing.Release();
        }
    }

    /// <summary>
    /// One statement per bucket, correct under concurrency, where EF's
    /// read-modify-write would lose an increment. The schema is a compile-time
    /// constant and the values are EF positional parameters, so nothing here is
    /// concatenated from a request.
    /// </summary>
    private static readonly string AddUsageSql = $$"""
        INSERT INTO {{McpDbContext.Schema}}."TokenUsage" ("TokenId", "Hour", "Count")
        VALUES ({0}, {1}, {2})
        ON CONFLICT ("TokenId", "Hour")
        DO UPDATE SET "Count" = "TokenUsage"."Count" + EXCLUDED."Count";
        """;

    private static readonly string LastUsedSql = $$"""
        UPDATE {{McpDbContext.Schema}}."Tokens" SET "LastUsedAt" = {1}
        WHERE "Id" = {0} AND ("LastUsedAt" IS NULL OR "LastUsedAt" < {1});
        """;
}

/// <summary>Writes <see cref="McpUsageCounter"/> out every minute, and once more on shutdown.</summary>
public sealed class McpUsageFlusher(
    McpUsageCounter counter,
    IServiceScopeFactory scopes,
    ILogger<McpUsageFlusher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    /// <summary>Never throws: a lost minute of usage counts must not stop the host.</summary>
    private async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await counter.FlushAsync(scope.ServiceProvider.GetRequiredService<McpDbContext>(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Couldn't write MCP token usage; it will be retried.");
        }
    }
}
