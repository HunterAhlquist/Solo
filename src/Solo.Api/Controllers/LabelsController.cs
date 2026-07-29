using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>
/// A project's label defaults. They are a template, not a live set: new boards
/// are stamped from them and a board reset returns to them, so editing one here
/// never changes a board that already exists.
/// </summary>
[Route("api/projects/{projectId:guid}/labels")]
public sealed class LabelsController(SoloDbContext db) : ApiControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<LabelDto>>> List(Guid projectId, CancellationToken ct)
    {
        var labels = await Defaults(projectId).AsNoTracking()
            .OrderBy(l => l.Kind).ThenBy(l => l.Name)
            .ToListAsync(ct);
        return labels.Select(l => l.ToDto()).ToList();
    }

    [HttpPost]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<LabelDto>> Create(
        Guid projectId, CreateLabelRequest request, CancellationToken ct)
    {
        var label = new Label
        {
            ProjectId = projectId,
            BoardId = null,
            Kind = request.Kind,
            Name = request.Name,
            Color = request.Color,
            Icon = request.Icon,
            ImageId = request.ImageId,
        };
        db.Labels.Add(label);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(List), new { projectId }, label.ToDto());
    }

    [HttpPut("{labelId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<LabelDto>> Update(
        Guid projectId, Guid labelId, UpdateLabelRequest request, CancellationToken ct)
    {
        var label = await Defaults(projectId).FirstOrDefaultAsync(l => l.Id == labelId, ct);
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

        return label.ToDto();
    }

    [HttpDelete("{labelId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<IActionResult> Delete(Guid projectId, Guid labelId, CancellationToken ct)
    {
        var label = await Defaults(projectId).FirstOrDefaultAsync(l => l.Id == labelId, ct);
        if (label is null)
        {
            return NotFound();
        }

        db.Labels.Remove(label);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IQueryable<Label> Defaults(Guid projectId) =>
        db.Labels.Where(l => l.ProjectId == projectId && l.BoardId == null);
}
