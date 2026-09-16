using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Solo.Extensibility;
using Solo.Mcp.Auth;
using Solo.Mcp.Data;

namespace Solo.Mcp.Auditing;

/// <summary>MCP attribution lives entirely in the extension's schema.</summary>
public sealed class McpAuditContributor(IHttpContextAccessor http, McpDbContext db) : ISoloAuditContributor
{
    private Guid? TokenId => http.HttpContext?.User.Identity?.AuthenticationType == McpAuthentication.Scheme &&
        Guid.TryParse(http.HttpContext.User.FindFirstValue(McpAuthentication.TokenIdClaim), out var id) ? id : null;

    public bool ShouldRecord => TokenId is not null;

    public async Task RecordAsync(IReadOnlyList<AuditRecord> entries, DbConnection connection,
        DbTransaction transaction, CancellationToken ct)
    {
        if (TokenId is not { } tokenId) return;
        // A separate context shares only the transaction, never the host's
        // entity model or the authentication context's tracked token records.
        await using var writer = new McpDbContext(new DbContextOptionsBuilder<McpDbContext>()
            .UseNpgsql(connection).Options);
        await writer.Database.UseTransactionAsync(transaction, ct);
        writer.AuditAttributions.AddRange(entries.Select(e => new McpAuditAttribution
        {
            AuditEntryId = e.Id,
            TokenId = tokenId,
        }));
        await writer.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<long, string>> ReadAsync(
        IReadOnlyList<long> auditIds, CancellationToken ct)
    {
        var ids = await db.AuditAttributions.AsNoTracking().Where(a => auditIds.Contains(a.AuditEntryId))
            .Select(a => a.AuditEntryId).ToListAsync(ct);
        return ids.ToDictionary(id => id, _ => "LLM via MCP");
    }
}
