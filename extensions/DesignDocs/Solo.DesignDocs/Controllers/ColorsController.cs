using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.Extensibility;

namespace Solo.DesignDocs.Controllers;

/// <summary>
/// Each member's cursor color in a project: a slot given in the order people
/// first open a doc there, which the client turns into a hue by golden-angle
/// steps, so nobody's color changes when someone new joins.
/// </summary>
[Route("projects/{projectId:guid}/colors")]
public sealed class ColorsController(DesignDocsDbContext db, ISoloQuery query, ISoloLiveUpdates live)
    : DocsControllerBase(db, live)
{
    [HttpGet]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<IReadOnlyList<MemberColorDto>> List(Guid projectId, CancellationToken ct) =>
        await Db.MemberColors.AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .OrderBy(c => c.Slot)
            .Select(c => new MemberColorDto(c.UserId, c.Slot))
            .ToListAsync(ct);

    /// <summary>The caller's slot, given now if they have none. Asking again returns the same one.</summary>
    [HttpPost]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<MemberColorDto> Claim(Guid projectId, CancellationToken ct)
    {
        var userId = (await query.CallerAsync(ct)).UserId;
        for (var attempt = 0; ; attempt++)
        {
            var existing = await Db.MemberColors.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.UserId == userId, ct);
            if (existing is not null)
            {
                return new MemberColorDto(userId, existing.Slot);
            }

            var next = await Db.MemberColors.Where(c => c.ProjectId == projectId).MaxAsync(c => (int?)c.Slot, ct) + 1 ?? 0;
            var color = new MemberColor { ProjectId = projectId, UserId = userId, Slot = next };
            Db.MemberColors.Add(color);
            try
            {
                await Db.SaveChangesAsync(ct);
                return new MemberColorDto(userId, next);
            }
            catch (DbUpdateException e) when (IsUniqueViolation(e) && attempt < 8)
            {
                // Someone took that slot, or this person claimed in another tab.
                Db.Entry(color).State = EntityState.Detached;
            }
        }
    }
}
