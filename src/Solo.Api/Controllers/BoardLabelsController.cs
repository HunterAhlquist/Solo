using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Realtime;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Domain;
using Solo.Data.Entities;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Api.Controllers;

/// <summary>
/// A board's own labels — the only ones its tasks and sub-tasks can carry. A
/// board starts with a copy of the project defaults and diverges from there
/// until <see cref="Reset"/> puts it back.
/// </summary>
[Route("api/boards/{boardId:guid}/labels")]
public sealed class BoardLabelsController(SoloDbContext db, IBoardNotifier notifier) : ApiControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<LabelDto>>> List(Guid boardId, CancellationToken ct)
    {
        if (await ProjectOf(boardId, ct) is null)
        {
            return NotFound();
        }

        var labels = await Owned(boardId).AsNoTracking()
            .OrderBy(l => l.Kind).ThenBy(l => l.Name)
            .ToListAsync(ct);
        return labels.Select(l => l.ToDto()).ToList();
    }

    [HttpPost]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<LabelDto>> Create(
        Guid boardId, CreateLabelRequest request, CancellationToken ct)
    {
        if (await ProjectOf(boardId, ct) is not { } projectId)
        {
            return NotFound();
        }

        var label = new Label
        {
            ProjectId = projectId,
            BoardId = boardId,
            Kind = request.Kind,
            Name = request.Name,
            Color = request.Color,
            Icon = request.Icon,
            ImageId = request.ImageId,
        };
        db.Labels.Add(label);
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(boardId, EntityTypes.Label, label.Id, ChangeKind.Created);
        return CreatedAtAction(nameof(List), new { boardId }, label.ToDto());
    }

    [HttpPut("{labelId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<LabelDto>> Update(
        Guid boardId, Guid labelId, UpdateLabelRequest request, CancellationToken ct)
    {
        var label = await Owned(boardId).FirstOrDefaultAsync(l => l.Id == labelId, ct);
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

        await notifier.BoardEntityChanged(boardId, EntityTypes.Label, label.Id,
            ChangeKind.Updated, nameof(label.Name), nameof(label.Color),
            nameof(label.Icon), nameof(label.ImageId));
        return label.ToDto();
    }

    [HttpDelete("{labelId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<IActionResult> Delete(Guid boardId, Guid labelId, CancellationToken ct)
    {
        var label = await Owned(boardId).FirstOrDefaultAsync(l => l.Id == labelId, ct);
        if (label is null)
        {
            return NotFound();
        }

        db.Labels.Remove(label);
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(boardId, EntityTypes.Label, labelId, ChangeKind.Deleted);
        return NoContent();
    }

    /// <summary>
    /// Discards this board's label edits and returns to the project defaults.
    /// A default that matches an existing label by kind and name is applied to
    /// that label rather than replacing it, so items keep their assignment;
    /// labels the defaults don't name are deleted and their items lose theirs.
    /// </summary>
    [HttpPost("reset")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<IReadOnlyList<LabelDto>>> Reset(Guid boardId, CancellationToken ct)
    {
        if (await ProjectOf(boardId, ct) is not { } projectId)
        {
            return NotFound();
        }

        var defaults = await db.Labels.AsNoTracking()
            .Where(l => l.ProjectId == projectId && l.BoardId == null)
            .ToListAsync(ct);
        var current = await Owned(boardId).ToListAsync(ct);

        var result = BoardLabels.ResetToDefaults(defaults, current, boardId);
        db.Labels.AddRange(result.Added);
        db.Labels.RemoveRange(result.Removed);
        await db.SaveChangesAsync(ct);

        await notifier.BoardEntityChanged(boardId, EntityTypes.Label, boardId, ChangeKind.Updated);
        return current.Except(result.Removed).Concat(result.Added)
            .OrderBy(l => l.Kind).ThenBy(l => l.Name)
            .Select(l => l.ToDto())
            .ToList();
    }

    private IQueryable<Label> Owned(Guid boardId) => db.Labels.Where(l => l.BoardId == boardId);

    private async Task<Guid?> ProjectOf(Guid boardId, CancellationToken ct)
    {
        var projectId = await db.Boards.AsNoTracking()
            .Where(b => b.Id == boardId)
            .Select(b => (Guid?)b.ProjectId)
            .FirstOrDefaultAsync(ct);
        return projectId;
    }
}
