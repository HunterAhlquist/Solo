using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Realtime;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Api.Controllers;

/// <summary>Global + board-local label config for a project.</summary>
[Route("api/projects/{projectId:guid}/labels")]
public sealed class LabelsController(SoloDbContext db, IBoardNotifier notifier) : ApiControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<LabelDto>>> List(
        Guid projectId, [FromQuery] Guid? boardId, CancellationToken ct)
    {
        var query = db.Labels.AsNoTracking().Where(l => l.ProjectId == projectId);
        if (boardId is not null)
        {
            query = query.Where(l => l.BoardId == null || l.BoardId == boardId);
        }

        var labels = await query.OrderBy(l => l.Kind).ThenBy(l => l.Name).ToListAsync(ct);
        return labels.Select(l => l.ToDto()).ToList();
    }

    [HttpPost]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<LabelDto>> Create(
        Guid projectId, CreateLabelRequest request, CancellationToken ct)
    {
        if (request.BoardId is not null)
        {
            var boardInProject = await db.Boards.AsNoTracking()
                .AnyAsync(b => b.Id == request.BoardId && b.ProjectId == projectId, ct);
            if (!boardInProject)
            {
                return Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Unknown board",
                    detail: "Board-local labels must reference a board in this project.");
            }
        }

        var label = new Label
        {
            ProjectId = projectId,
            BoardId = request.BoardId,
            Kind = request.Kind,
            Name = request.Name,
            Color = request.Color,
            Icon = request.Icon,
            ImageId = request.ImageId,
        };
        db.Labels.Add(label);
        await db.SaveChangesAsync(ct);
        await notifier.ProjectEntityChanged(projectId, EntityTypes.Label, label.Id, ChangeKind.Created);
        return CreatedAtAction(nameof(List), new { projectId }, label.ToDto());
    }

    [HttpPut("{labelId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<LabelDto>> Update(
        Guid projectId, Guid labelId, UpdateLabelRequest request, CancellationToken ct)
    {
        var label = await db.Labels
            .FirstOrDefaultAsync(l => l.ProjectId == projectId && l.Id == labelId, ct);
        if (label is null)
        {
            return NotFound();
        }

        label.Name = request.Name;
        label.Color = request.Color;
        label.Icon = request.Icon;
        label.ImageId = request.ImageId;
        UseClientVersion(db, label, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await notifier.ProjectEntityChanged(projectId, EntityTypes.Label, label.Id,
            ChangeKind.Updated, nameof(label.Name), nameof(label.Color),
            nameof(label.Icon), nameof(label.ImageId));
        return label.ToDto();
    }

    [HttpDelete("{labelId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<IActionResult> Delete(Guid projectId, Guid labelId, CancellationToken ct)
    {
        var label = await db.Labels
            .FirstOrDefaultAsync(l => l.ProjectId == projectId && l.Id == labelId, ct);
        if (label is null)
        {
            return NotFound();
        }

        db.Labels.Remove(label);
        await db.SaveChangesAsync(ct);
        await notifier.ProjectEntityChanged(projectId, EntityTypes.Label, labelId, ChangeKind.Deleted);
        return NoContent();
    }
}
