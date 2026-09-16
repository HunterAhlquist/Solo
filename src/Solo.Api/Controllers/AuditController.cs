using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Data;
using Solo.Extensions;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>Paged, filterable audit query — project owners and the super admin.</summary>
[Route("api/projects/{projectId:guid}/audit")]
[Authorize(Policy = AuthConstants.ProjectOwnerPolicy)]
public sealed class AuditController(SoloDbContext db, ExtensionAuditContributions contributions) : ApiControllerBase
{
    private const int MaxPageSize = 200;

    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditEntryDto>>> Query(
        Guid projectId,
        [FromQuery] string? table,
        [FromQuery] Guid? userId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.AuditEntries.AsNoTracking().Where(a => a.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(table))
        {
            query = query.Where(a => a.TableName == table);
        }

        if (userId is not null)
        {
            query = query.Where(a => a.UserId == userId);
        }

        if (from is not null)
        {
            var fromUtc = AsUtc(from.Value);
            query = query.Where(a => a.Timestamp >= fromUtc);
        }

        if (to is not null)
        {
            var toUtc = AsUtc(to.Value);
            query = query.Where(a => a.Timestamp < toUtc);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .GroupJoin(db.Users, a => a.UserId, u => (Guid?)u.Id, (a, users) => new { a, users })
            .SelectMany(x => x.users.DefaultIfEmpty(), (x, u) => new AuditEntryDto(
                x.a.Id, x.a.Timestamp, x.a.UserId,
                u != null ? u.DisplayName : null,
                x.a.TableName, x.a.RowId, x.a.ColumnName, x.a.PreviousValue))
            .ToListAsync(ct);

        return new PagedResult<AuditEntryDto>(await contributions.AnnotateAsync(items, ct), page, pageSize, total);
    }

    /// <summary>Npgsql maps timestamptz to UTC-kind DateTime only.</summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
