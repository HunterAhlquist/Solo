using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Contract 1.8's member read, anonymous for the same reason as above.</summary>
    [HttpGet("members/{projectId:guid}")]
    [AllowAnonymous]
    public async Task<IReadOnlyList<string>> Members(Guid projectId, CancellationToken ct) =>
        (await query.MembersAsync(projectId, ct)).Select(m => m.Email).ToList();

    [HttpGet("comments/{commentId:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<Solo.Shared.Dtos.CommentDto>> Comment(Guid commentId, CancellationToken ct) =>
        await query.CommentAsync(commentId, ct) is { } comment ? comment : NotFound();

    [HttpGet("task-comments/{taskId:guid}")]
    [AllowAnonymous]
    public async Task<IReadOnlyList<string>> TaskComments(Guid taskId, CancellationToken ct) =>
        (await query.TaskCommentsAsync(taskId, ct)).Select(c => c.Body).ToList();

    /// <summary>
    /// Where a person turns on the fixture's background work. A refusal — from
    /// background work itself, say — comes back as a 409 the test can read.
    /// </summary>
    [HttpPost("background-grant")]
    [Authorize]
    public async Task<IActionResult> Grant(
        [FromKeyedServices("fixture")] ISoloBackground background, CancellationToken ct)
    {
        try
        {
            await background.GrantAsync(ct);
            return NoContent();
        }
        catch (InvalidOperationException e)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Not granted", detail: e.Message);
        }
    }
}
