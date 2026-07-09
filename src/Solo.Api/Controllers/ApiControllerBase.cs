using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solo.Data;
using Solo.Data.Entities;

namespace Solo.Api.Controllers;

[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>True when the failed save tripped the named unique index.</summary>
    protected static bool IsUniqueViolation(DbUpdateException e, string indexName) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
        pg.ConstraintName == indexName;

    /// <summary>409 for an alias that another sibling already claimed.</summary>
    protected ObjectResult AliasTakenProblem(string siblingScope) => Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Alias already in use",
        detail: $"Another {siblingScope} already uses this alias. Pick a different one.");

    /// <summary>
    /// Applies the client's optimistic-concurrency token before a save so a
    /// stale edit surfaces as <see cref="DbUpdateConcurrencyException"/>.
    /// </summary>
    protected static void UseClientVersion<TEntity>(SoloDbContext db, TEntity entity, uint clientVersion)
        where TEntity : class, IVersioned
    {
        db.Entry(entity).Property(e => e.Version).OriginalValue = clientVersion;
    }

    /// <summary>
    /// Saves and converts a concurrency-token mismatch into 409 Conflict so the
    /// client can re-fetch and merge.
    /// </summary>
    protected async Task<ObjectResult?> SaveOrConflictAsync(SoloDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Concurrency conflict",
                detail: "The item was modified by someone else. Re-fetch and merge your changes.");
        }
    }
}
