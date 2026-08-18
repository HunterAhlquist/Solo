using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solo.Data;
using Solo.Data.Entities;

namespace Solo.Extensions;

/// <summary>
/// Reads and writes the core-side record of which extensions this instance has
/// seen, and performs the destructive half of uninstall.
/// </summary>
/// <remarks>
/// The registry lives in core's schema rather than being derived from the
/// packages on disk, because the case worth handling is exactly the one where
/// the package is gone: delete a zip, restart, and without a row the schema it
/// left behind is an orphan nobody can attribute or offer to clean up.
/// </remarks>
public sealed class ExtensionRegistry(SoloDbContext db, ILogger<ExtensionRegistry> logger)
{
    /// <summary>
    /// Reconciles the registry against what actually loaded this boot: records
    /// each loaded extension, and marks anything with surviving data whose
    /// package is no longer present as <see cref="ExtensionRegistrationState.Absent"/>.
    /// </summary>
    public async Task ReconcileAsync(ExtensionState state, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.ExtensionRegistrations.ToListAsync(ct);
        var present = new HashSet<string>(state.Packages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var package in state.Packages)
        {
            var row = rows.FirstOrDefault(r =>
                string.Equals(r.ExtensionId, package.Id, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new ExtensionRegistration { ExtensionId = package.Id, InstalledAt = now };
                db.ExtensionRegistrations.Add(row);
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

        // Rows whose package did not turn up this boot. Their data is still
        // there — that is the whole point of keeping the row.
        foreach (var row in rows.Where(r => !present.Contains(r.ExtensionId)))
        {
            row.State = ExtensionRegistrationState.Absent;
            row.FailureReason = null;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Refuses to load a package older than the one that last migrated this
    /// database.
    /// </summary>
    /// <remarks>
    /// EF has no notion of a history row being "too new": it applies whatever
    /// migrations the assembly knows and ignores the rest, so a downgrade
    /// otherwise runs a stale model against a newer schema and fails somewhere
    /// far from the cause.
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
    /// Drops an extension's schema and strips its keys from every core field
    /// bag, then forgets the registration.
    /// </summary>
    /// <remarks>
    /// <para>Both halves or it is not a purge. The jsonb bags live in CORE
    /// tables, so dropping the schema alone would leave a key on every task
    /// forever.</para>
    ///
    /// <para>Callers must have established that the extension is not loaded this
    /// boot — see <c>ExtensionsController</c>. That is what makes dropping the
    /// schema safe without any locking: nothing in the process holds a
    /// DbContext against it.</para>
    /// </remarks>
    public async Task PurgeAsync(ExtensionRegistration registration, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(registration.SchemaName))
        {
            // The schema name is derived from the extension id at load and
            // re-derived here rather than trusted from the row, so this can
            // never be pointed at core's schema or another extension's.
            var schema = ExtensionPackageLoader.ExpectedSchema(registration.ExtensionId);
            if (!string.Equals(schema, registration.SchemaName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Registered schema '{registration.SchemaName}' does not match the name derived from " +
                    $"extension id '{registration.ExtensionId}'. Refusing to drop it.");
            }

            logger.LogWarning("Dropping schema {Schema} for extension {Id}.", schema, registration.ExtensionId);

            // EF1002: a schema name cannot be a SQL parameter. This one is not
            // caller input — it is re-derived above from the extension id, which
            // the package loader has already constrained to [a-z0-9-], and the
            // equality check above refuses anything that does not match.
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
    /// <c>jsonb - 'key'</c> is a no-op on a document that lacks the key, so no
    /// existence test is needed — which also keeps the jsonb <c>?</c> operator,
    /// awkward to pass through parameterised SQL, out of the statement.
    /// </remarks>
    private async Task StripFieldBagsAsync(string extensionId, CancellationToken ct)
    {
        // Postgres only. The unit-test SQLite provider stores these bags as
        // plain text and has no jsonb operators, so it never reaches this path.
        if (!db.Database.IsNpgsql())
        {
            return;
        }

        foreach (var table in ExtensionFieldStore.BagTables)
        {
            // EF1002: a table name cannot be a SQL parameter. These come from a
            // private array of literals, never from input; the extension id, the
            // one value that does vary, is passed as a parameter.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{table}\" SET \"Extensions\" = \"Extensions\" - {{0}} WHERE \"Extensions\" IS NOT NULL",
                [extensionId],
                ct);
#pragma warning restore EF1002
        }
    }
}
