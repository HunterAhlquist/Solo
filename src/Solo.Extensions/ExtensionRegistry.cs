using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solo.Data;
using Solo.Data.Entities;

namespace Solo.Extensions;

/// <summary>
/// Tracks extensions seen by this instance and purges their stored data.
/// </summary>
/// <remarks>
/// The registry survives package removal so its schema can still be identified
/// and purged.
/// </remarks>
public sealed class ExtensionRegistry(SoloDbContext db, ILogger<ExtensionRegistry> logger)
{
    /// <summary>
    /// Updates packages found at startup and marks missing packages as
    /// <see cref="ExtensionRegistrationState.Absent"/>.
    /// </summary>
    public async Task ReconcileAsync(ExtensionState state, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.ExtensionRegistrations.ToListAsync(ct);
        var rowsById = rows.ToDictionary(r => r.ExtensionId, StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(state.Packages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var package in state.Packages)
        {
            if (!rowsById.TryGetValue(package.Id, out var row))
            {
                row = new ExtensionRegistration { ExtensionId = package.Id, InstalledAt = now };
                db.ExtensionRegistrations.Add(row);
                rowsById.Add(package.Id, row);
            }

            row.SchemaName = package.Manifest.Schema;
            row.DisplayName = package.Manifest.Name;
            row.LastSeenVersion = package.Manifest.Version;
            row.LastLoadedAt = now;

            if (state.IsDisabled(package.Id, out var reason))
            {
                row.State = ExtensionRegistrationState.Failed;
                row.FailureReason = reason;
            }
            else
            {
                row.State = ExtensionRegistrationState.Loaded;
                row.FailureReason = null;
            }
        }

        // Missing packages may still have stored data.
        foreach (var row in rows.Where(r => !present.Contains(r.ExtensionId)))
        {
            row.State = ExtensionRegistrationState.Absent;
            row.FailureReason = null;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Rejects a package older than the last version recorded for its database.
    /// </summary>
    /// <remarks>
    /// EF applies known migrations and ignores newer history rows. A downgrade
    /// could therefore run a stale model against a newer schema.
    /// </remarks>
    public async Task<string?> CheckDowngradeAsync(ExtensionPackage package, CancellationToken ct = default)
    {
        var row = await db.ExtensionRegistrations.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ExtensionId == package.Id, ct);
        if (row?.LastSeenVersion is null)
        {
            return null;
        }

        if (!Version.TryParse(row.LastSeenVersion, out var seen) ||
            !Version.TryParse(package.Manifest.Version, out var offered))
        {
            return null;
        }

        return offered < seen
            ? $"Package is version {offered} but this database was last migrated by {seen}. " +
              "Install {seen} or newer, or purge the extension's data first."
            : null;
    }

    public Task<List<ExtensionRegistration>> ListAsync(CancellationToken ct = default) =>
        db.ExtensionRegistrations.AsNoTracking().OrderBy(r => r.ExtensionId).ToListAsync(ct);

    /// <summary>
    /// Drops an extension's schema, clears its core fields, and deletes its registration.
    /// </summary>
    /// <remarks>
    /// <para>The jsonb bags live in core tables, so dropping only the extension
    /// schema would leave its field keys behind.</para>
    ///
    /// <para>Callers must first confirm the extension is not loaded. No process
    /// then holds its DbContext, so the schema can be dropped without locking.
    /// See <c>ExtensionsController</c>.</para>
    /// </remarks>
    public async Task PurgeAsync(ExtensionRegistration registration, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(registration.SchemaName))
        {
            // Re-derive and compare the schema so a stored value cannot target
            // core or another extension.
            var schema = ExtensionPackageLoader.ExpectedSchema(registration.ExtensionId);
            if (!string.Equals(schema, registration.SchemaName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Registered schema '{registration.SchemaName}' does not match the name derived from " +
                    $"extension id '{registration.ExtensionId}'. Refusing to drop it.");
            }

            logger.LogWarning("Dropping schema {Schema} for extension {Id}.", schema, registration.ExtensionId);

            // EF1002: schema names cannot be parameters. The loader restricts
            // the id to [a-z0-9-], and the derived name must match the stored one.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", ct);
#pragma warning restore EF1002
        }

        await StripFieldBagsAsync(registration.ExtensionId, ct);

        db.ExtensionRegistrations.Remove(
            await db.ExtensionRegistrations.FirstAsync(r => r.ExtensionId == registration.ExtensionId, ct));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes one extension's key from the jsonb bags on tasks, epics and boards.</summary>
    /// <remarks>
    /// <c>jsonb - 'key'</c> ignores missing keys. This avoids an existence query
    /// and its hard-to-parameterize <c>?</c> operator.
    /// </remarks>
    private async Task StripFieldBagsAsync(string extensionId, CancellationToken ct)
    {
        // SQLite tests store these bags as text and cannot run jsonb operators.
        if (!db.Database.IsNpgsql())
        {
            return;
        }

        foreach (var table in ExtensionFieldStore.BagTables)
        {
            // EF1002: table names cannot be parameters. BagTables contains only
            // private literals; extensionId remains parameterized.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{table}\" SET \"Extensions\" = \"Extensions\" - {{0}} WHERE \"Extensions\" IS NOT NULL",
                [extensionId],
                ct);
#pragma warning restore EF1002
        }
    }
}
