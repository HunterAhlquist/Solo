using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Solo.Roadmap.Data;

/// <summary>Reads the settings row, creating it with the defaults the first time anyone asks.</summary>
public sealed class RoadmapSettingsStore(RoadmapDbContext db)
{
    /// <summary>The settings row, tracked, so a caller can edit and save it.</summary>
    public async Task<RoadmapSettings> GetAsync(CancellationToken ct = default)
    {
        var settings = await db.Settings.FirstOrDefaultAsync(s => s.Id == RoadmapSettings.SingletonId, ct);
        if (settings is not null)
        {
            return settings;
        }

        settings = new RoadmapSettings();
        db.Settings.Add(settings);
        try
        {
            await db.SaveChangesAsync(ct);
            return settings;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
                                              { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two first reads raced; the other one's row is as good as ours.
            db.Entry(settings).State = EntityState.Detached;
            return await db.Settings.FirstAsync(s => s.Id == RoadmapSettings.SingletonId, ct);
        }
    }
}
