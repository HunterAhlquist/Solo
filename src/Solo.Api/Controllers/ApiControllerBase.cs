using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Data.Entities;

namespace Solo.Api.Controllers;

[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
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
