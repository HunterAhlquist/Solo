using SoloExtension_CSharp.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Extensibility;

namespace SoloExtension_CSharp;

/// <summary>
/// Reachable at <c>/api/ext/solo-extension-id/projects/{projectId}/notes</c>.
/// </summary>
/// <remarks>
/// The route carries a project id, which is what lets the project policies
/// work: they resolve the project from the route and check the caller's access
/// to it before the action runs.
/// </remarks>
[ApiController]
[Route("projects/{projectId:guid}/notes")]
public sealed class NotesController(StarterDbContext db, ISoloQuery query) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<IReadOnlyList<NoteDto>> List(Guid projectId, CancellationToken ct) =>
        await db.Notes.AsNoTracking()
            .Where(n => n.ProjectId == projectId)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new NoteDto(n.Id, n.Text, n.CreatedByUserId, n.CreatedAt))
            .ToListAsync(ct);

    [HttpPost]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<NoteDto>> Create(Guid projectId, CreateNoteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 2000)
        {
            return Problem(statusCode: 400, title: "Invalid note", detail: "A note needs 1 to 2000 characters.");
        }

        var caller = await query.CallerAsync(ct);
        var note = new Note
        {
            ProjectId = projectId,
            Text = request.Text.Trim(),
            CreatedByUserId = caller.UserId,
        };

        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);

        return new NoteDto(note.Id, note.Text, note.CreatedByUserId, note.CreatedAt);
    }
}
