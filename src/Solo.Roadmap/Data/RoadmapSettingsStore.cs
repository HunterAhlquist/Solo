using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Solo.Roadmap.Data;

/// <summary>Reads the settings row, creating it with the defaults the first time anyone asks.</summary>
/// <remarks>
/// Created on first read rather than seeded by the migration: the stripe anchor
/// defaults to 1 January of the year the row appears, and a migration would bake
/// in the year the package was built instead.
/// </remarks>
public sealed class RoadmapSettingsStore(RoadmapDbContext db, TimeProvider clock)
{
    /// <summary>The settings row, tracked, so a caller can edit and save it.</summary>
    public async Task<RoadmapSettings> GetAsync(CancellationToken ct = default)
    {
        var settings = await db.Settings.FirstOrDefaultAsync(s => s.Id == RoadmapSettings.SingletonId, ct);
        if (settings is not null)
        {
            return settings;
        }

        settings = new RoadmapSettings { StripeAnchor = new DateOnly(clock.GetUtcNow().Year, 1, 1) };
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

    /// <summary>
    /// The anchor a day of year names in <paramref name="year"/>, or null when the
    /// year has no such day — 366 outside a leap year.
    /// </summary>
    public static DateOnly? ResolveAnchor(int dayOfYear, int year) =>
        dayOfYear >= 1 && dayOfYear <= (DateTime.IsLeapYear(year) ? 366 : 365)
            ? new DateOnly(year, 1, 1).AddDays(dayOfYear - 1)
            : null;

    /// <summary>The year a saved day of year resolves in.</summary>
    public int CurrentYear => clock.GetUtcNow().Year;
}
