using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Extensibility;

namespace Solo.Extension.Fixture;

/// <summary>
/// Declares a route that would shadow a core endpoint if the host trusted it.
/// The route convention rewrites it to <c>/api/ext/fixture/…</c>, which is what
/// <c>ExtensionApiTests</c> asserts.
/// </summary>
[ApiController]
[Route("api/tasks")]
public sealed class FixtureController(FixtureDbContext db, ISoloQuery query) : ControllerBase
{
    /// <summary>
    /// Reachable at <c>/api/ext/fixture/api/tasks/milestones</c>. Plain
    /// <c>[Authorize]</c> because the route carries no project to resolve — the
    /// project policies need one. Project-scoped reads go through ISoloQuery.
    /// </summary>
    [HttpGet("milestones")]
    [Authorize]
    public async Task<IReadOnlyList<string>> Milestones(CancellationToken ct) =>
        await db.Milestones.OrderBy(m => m.Name).Select(m => m.Name).ToListAsync(ct);

    [HttpPost("milestones")]
    [Authorize]
    public async Task<IActionResult> Add([FromQuery] string name, CancellationToken ct)
    {
        db.Milestones.Add(new Milestone { Name = name });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Proves ISoloQuery is scoped to the caller: an extension asking for boards
    /// of a project the caller is not a member of gets an empty list, not rows.
    /// </summary>
    [HttpGet("visible-boards/{projectId:guid}")]
    [AllowAnonymous]
    public async Task<IReadOnlyList<Guid>> VisibleBoards(Guid projectId, CancellationToken ct) =>
        (await query.BoardsAsync(projectId, ct)).Select(b => b.Id).ToList();
}
