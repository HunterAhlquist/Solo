using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Realtime;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Extensibility;
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
    [SoloOperation(Operations.EpicCreate)]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<EpicDto>> Create(
        Guid boardId, CreateEpicRequest request, CancellationToken ct)
    {
        if (!await db.Boards.AsNoTracking().AnyAsync(b => b.Id == boardId, ct))
        {
            return NotFound();
        }

        if (request.GroupId is { } groupId &&
            !await db.EpicGroups.AnyAsync(g => g.Id == groupId && g.BoardId == boardId, ct))
            return BadRequest("The group must belong to this board.");
        var siblings = await EpicRanking.Siblings(db, boardId, request.GroupId, ct);
        var epic = new Epic
        {
            BoardId = boardId,
            Name = request.Name,
            Description = request.Description,
            GroupId = request.GroupId,
            Rank = Ranking.ForAppend(siblings.Select(i => i.Rank)),
        };
        db.Epics.Add(epic);
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(boardId, EntityTypes.Epic, epic.Id, ChangeKind.Created);
        return CreatedAtAction(nameof(List), new { boardId }, epic.ToDto());
    }

    [HttpPut("api/epics/{epicId:guid}")]
    [SoloOperation(Operations.EpicUpdate)]
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
    [SoloOperation(Operations.EpicDelete)]
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

        var siblings = await EpicRanking.Siblings(db, epic.BoardId, epic.GroupId, ct);
        if (!EpicRanking.Move(siblings, epicId, request.PrecedingId, request.FollowingId))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid rank target",
                detail: "The neighbor ids must belong to the same group or top-level board order.");
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

    [HttpPut("api/epics/{epicId:guid}/group")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<EpicDto>> Move(Guid epicId, MoveEpicRequest request, CancellationToken ct)
    {
        var epic = await db.Epics.Include(e => e.Assignments).FirstOrDefaultAsync(e => e.Id == epicId, ct);
        if (epic is null) return NotFound();
        if (request.GroupId is { } groupId &&
            !await db.EpicGroups.AnyAsync(g => g.Id == groupId && g.BoardId == epic.BoardId, ct))
            return BadRequest("The group must belong to this board.");
        var siblings = await EpicRanking.Siblings(db, epic.BoardId, request.GroupId, ct);
        if (siblings.All(i => i.Id != epicId)) siblings.Add(EpicRankItem.For(epic));
        if (!EpicRanking.Move(siblings, epicId, request.PrecedingId, request.FollowingId))
            return BadRequest("The neighbors must belong to the destination group or board.");
        epic.GroupId = request.GroupId;
        UseClientVersion(db, epic, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null) return conflict;
        await notifier.BoardEntityChanged(epic.BoardId, EntityTypes.Epic, epicId,
            ChangeKind.Updated, nameof(epic.GroupId), nameof(epic.Rank));
        return epic.ToDto();
    }

    [HttpPut("api/epics/{epicId:guid}/assignees")]
    [SoloOperation(Operations.EpicSetAssignees)]
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

        var invalid = await AssignmentHelper.InvalidAssignees(db, ProjectId(), request.UserIds, ct);
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
