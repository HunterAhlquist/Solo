using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Data.Entities;

namespace Solo.Api.Services;

/// <summary>
/// Runs daily and deletes audit entries older than
/// <see cref="InstanceSettings.AuditRetentionDays"/>. Retention is the only
/// audit knob.
/// </summary>
public sealed class AuditRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditRetentionService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so migrations finish first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
                var retentionDays = await db.InstanceSettings
                    .Where(s => s.Id == InstanceSettings.SingletonId)
                    .Select(s => s.AuditRetentionDays)
                    .FirstOrDefaultAsync(stoppingToken);
                if (retentionDays <= 0)
                {
                    retentionDays = 90;
                }

                var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
                var deleted = await db.AuditEntries
                    .Where(a => a.Timestamp < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation(
                        "Audit retention removed {Count} entries older than {Cutoff:u}.", deleted, cutoff);
                }

                // Same daily sweep tidies dead API sessions. Expired rows are
                // already refused at authentication; this is just hygiene.
                var expiredSessions = await db.AuthSessions
                    .Where(s => s.ExpiresAt < DateTimeOffset.UtcNow)
                    .ExecuteDeleteAsync(stoppingToken);
                if (expiredSessions > 0)
                {
                    logger.LogInformation(
                        "Removed {Count} expired API sessions.", expiredSessions);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit retention sweep failed; will retry next interval.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
