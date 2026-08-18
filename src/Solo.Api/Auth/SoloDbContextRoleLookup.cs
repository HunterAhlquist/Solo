using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Shared;

namespace Solo.Api.Auth;

/// <summary>One membership lookup, shared by the extension caller accessor.</summary>
public sealed class SoloDbContextRoleLookup(SoloDbContext db)
{
    public async Task<(ProjectRole? Role, bool IsOwner)> ForAsync(
        Guid userId, Guid projectId, CancellationToken ct)
    {
        var membership = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId && m.UserId == userId)
            .Select(m => new { m.Role, m.IsOwner })
            .FirstOrDefaultAsync(ct);
        return (membership?.Role, membership?.IsOwner ?? false);
    }
}
