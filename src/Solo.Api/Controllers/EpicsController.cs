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

public sealed class EpicsController(SoloDbContext db, IBoardNotifier notifier) : ApiControllerBase
{
    [HttpGet("api/boards/{boardId:guid}/epics")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<EpicDto>>> List(Guid boardId, CancellationToken ct)
    {
        var epics = await db.Epics.AsNoTracking()
            .Where(e => e.BoardId == boardId)
            .Include(e => e.Assignments)
            .OrderBy(e => e.Rank)
            .ToListAsync(ct);
        return epics.Select(e => e.ToDto()).ToList();
    }

    [HttpPost("api/boards/{boardId:guid}/epics")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<EpicDto>> Create(
        Guid boardId, CreateEpicRequest request, CancellationToken ct)
    {
        if (!await db.Boards.AsNoTracking().AnyAsync(b => b.Id == boardId, ct))
        {
            return NotFound();
        }

        var existingRanks = await db.Epics.AsNoTracking()
            .Where(e => e.BoardId == boardId).Select(e => e.Rank).ToListAsync(ct);
        var epic = new Epic
        {
            BoardId = boardId,
            Name = request.Name,
            Description = request.Description,
            Rank = Ranking.ForAppend(existingRanks),
        };
        db.Epics.Add(epic);
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(boardId, EntityTypes.Epic, epic.Id, ChangeKind.Created);
        return CreatedAtAction(nameof(List), new { boardId }, epic.ToDto());
    }

    [HttpPut("api/epics/{epicId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<EpicDto>> Update(
        Guid epicId, UpdateEpicRequest request, CancellationToken ct)
    {
        var epic = await db.Epics.Include(e => e.Assignments)
            .FirstOrDefaultAsync(e => e.Id == epicId, ct);
        if (epic is null)
        {
            return NotFound();
        }

        epic.Name = request.Name;
        epic.Description = request.Description;
        UseClientVersion(db, epic, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await notifier.BoardEntityChanged(epic.BoardId, EntityTypes.Epic, epicId,
            ChangeKind.Updated, nameof(epic.Name), nameof(epic.Description));
        return epic.ToDto();
    }

    /// <summary>Epic deletion detaches child tasks to ad-hoc rather than deleting them.</summary>
    [HttpDelete("api/epics/{epicId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<IActionResult> Delete(Guid epicId, CancellationToken ct)
    {
        var epic = await db.Epics.Include(e => e.Tasks)
            .FirstOrDefaultAsync(e => e.Id == epicId, ct);
        if (epic is null)
        {
            return NotFound();
        }

        var boardId = epic.BoardId;
        var detachedTaskIds = epic.Tasks.Select(t => t.Id).ToList();

        // Detach in the change tracker (not just ON DELETE SET NULL) so the
        // audit log preserves each task's EpicId change.
        foreach (var task in epic.Tasks.ToList())
        {
            task.EpicId = null;
        }

        db.Epics.Remove(epic);
        await db.SaveChangesAsync(ct);

        await notifier.BoardEntityChanged(boardId, EntityTypes.Epic, epicId, ChangeKind.Deleted);
        foreach (var taskId in detachedTaskIds)
        {
            await notifier.BoardEntityChanged(boardId, EntityTypes.Task, taskId,
                ChangeKind.Updated, nameof(TaskItem.EpicId));
        }

        return NoContent();
    }

    [HttpPut("api/epics/{epicId:guid}/rank")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<EpicDto>> Rerank(
        Guid epicId, RerankRequest request, CancellationToken ct)
    {
        var epic = await db.Epics.FirstOrDefaultAsync(e => e.Id == epicId, ct);
        if (epic is null)
        {
            return NotFound();
        }

        var siblings = await db.Epics
            .Where(e => e.BoardId == epic.BoardId)
            .OrderBy(e => e.Rank)
            .ToListAsync(ct);
        if (!Ranking.TryApplyMove(siblings, epicId, request.PrecedingId, request.FollowingId,
                e => e.Id, e => e.Rank, (e, r) => e.Rank = r))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid rank target",
                detail: "The neighbor ids are not epics on this board.");
        }

        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await notifier.BoardEntityChanged(epic.BoardId, EntityTypes.Epic, epicId,
            ChangeKind.Updated, nameof(epic.Rank));
        return epic.ToDto();
    }

    [HttpPut("api/epics/{epicId:guid}/assignees")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<EpicDto>> SetAssignees(
        Guid epicId, SetAssigneesRequest request, CancellationToken ct)
    {
        var epic = await db.Epics.Include(e => e.Assignments)
            .FirstOrDefaultAsync(e => e.Id == epicId, ct);
        if (epic is null)
        {
            return NotFound();
        }

        var projectId = (Guid)HttpContext.Items[ProjectAccessHandler.ProjectIdItem]!;
        var invalid = await AssignmentHelper.InvalidAssignees(db, projectId, request.UserIds, ct);
        if (invalid.Count > 0)
        {
            return AssignmentHelper.InvalidAssigneesProblem(this, invalid);
        }

        AssignmentHelper.Sync(db, epic.Assignments, request.UserIds, userId =>
            new Assignment { UserId = userId, EpicId = epicId });
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(epic.BoardId, EntityTypes.Epic, epicId,
            ChangeKind.Updated, "Assignees");
        return epic.ToDto();
    }
}
