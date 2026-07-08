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

public sealed class TasksController(SoloDbContext db, IBoardNotifier notifier) : ApiControllerBase
{
    [HttpGet("api/boards/{boardId:guid}/tasks")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<TaskDto>>> List(Guid boardId, CancellationToken ct)
    {
        var tasks = await db.Tasks.AsNoTracking()
            .Where(t => t.BoardId == boardId)
            .Include(t => t.SubTasks)
            .Include(t => t.Assignments)
            .OrderBy(t => t.Rank)
            .ToListAsync(ct);
        return tasks.Select(t => t.ToDto()).ToList();
    }

    [HttpGet("api/tasks/{taskId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<TaskDto>> Get(Guid taskId, CancellationToken ct)
    {
        var task = await LoadTask(taskId, ct, track: false);
        return task is null ? NotFound() : task.ToDto();
    }

    [HttpPost("api/boards/{boardId:guid}/tasks")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<TaskDto>> Create(
        Guid boardId, CreateTaskRequest request, CancellationToken ct)
    {
        var board = await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == boardId, ct);
        if (board is null)
        {
            return NotFound();
        }

        if (request.EpicId is not null &&
            !await db.Epics.AsNoTracking().AnyAsync(e => e.Id == request.EpicId && e.BoardId == boardId, ct))
        {
            return EpicNotOnBoardProblem();
        }

        var labelProblem = await ValidateLabel(request.LabelId, board.ProjectId, boardId, LabelKind.Task, ct);
        if (labelProblem is not null)
        {
            return labelProblem;
        }

        var existingRanks = await db.Tasks.AsNoTracking()
            .Where(t => t.BoardId == boardId).Select(t => t.Rank).ToListAsync(ct);
        var task = new TaskItem
        {
            BoardId = boardId,
            EpicId = request.EpicId,
            Name = request.Name,
            LabelId = request.LabelId,
            Description = request.Description,
            Rank = Ranking.ForAppend(existingRanks),
        };
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(boardId, EntityTypes.Task, task.Id, ChangeKind.Created);
        return CreatedAtAction(nameof(Get), new { taskId = task.Id }, task.ToDto());
    }

    [HttpPut("api/tasks/{taskId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<TaskDto>> Update(
        Guid taskId, UpdateTaskRequest request, CancellationToken ct)
    {
        var task = await LoadTask(taskId, ct, track: true);
        if (task is null)
        {
            return NotFound();
        }

        var labelProblem = await ValidateLabel(
            request.LabelId, ProjectId(), task.BoardId, LabelKind.Task, ct);
        if (labelProblem is not null)
        {
            return labelProblem;
        }

        task.Name = request.Name;
        task.LabelId = request.LabelId;
        task.Description = request.Description;
        UseClientVersion(db, task, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await notifier.BoardEntityChanged(task.BoardId, EntityTypes.Task, taskId,
            ChangeKind.Updated, nameof(task.Name), nameof(task.LabelId), nameof(task.Description));
        return task.ToDto();
    }

    [HttpDelete("api/tasks/{taskId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<IActionResult> Delete(Guid taskId, CancellationToken ct)
    {
        var task = await db.Tasks.Include(t => t.SubTasks)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
        {
            return NotFound();
        }

        var boardId = task.BoardId;
        db.Tasks.Remove(task);
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(boardId, EntityTypes.Task, taskId, ChangeKind.Deleted);
        return NoContent();
    }

    /// <summary>Column move. Rejected when the task has sub-tasks (status is derived).</summary>
    [HttpPut("api/tasks/{taskId:guid}/status")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<TaskDto>> SetStatus(
        Guid taskId, SetTaskStatusRequest request, CancellationToken ct)
    {
        var task = await LoadTask(taskId, ct, track: true);
        if (task is null)
        {
            return NotFound();
        }

        if (task.SubTasks.Count > 0)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Status is derived",
                detail: "This task has sub-tasks; its status is derived from them.");
        }

        task.Status = request.Status;
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(task.BoardId, EntityTypes.Task, taskId,
            ChangeKind.Updated, nameof(task.Status));
        return task.ToDto();
    }

    /// <summary>Attach to an epic on the same board, or null to detach to ad-hoc.</summary>
    [HttpPut("api/tasks/{taskId:guid}/epic")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<TaskDto>> SetEpic(
        Guid taskId, SetTaskEpicRequest request, CancellationToken ct)
    {
        var task = await LoadTask(taskId, ct, track: true);
        if (task is null)
        {
            return NotFound();
        }

        if (request.EpicId is not null &&
            !await db.Epics.AsNoTracking()
                .AnyAsync(e => e.Id == request.EpicId && e.BoardId == task.BoardId, ct))
        {
            return EpicNotOnBoardProblem();
        }

        task.EpicId = request.EpicId;
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(task.BoardId, EntityTypes.Task, taskId,
            ChangeKind.Updated, nameof(task.EpicId));
        return task.ToDto();
    }

    [HttpPut("api/tasks/{taskId:guid}/rank")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<TaskDto>> Rerank(
        Guid taskId, RerankRequest request, CancellationToken ct)
    {
        var task = await LoadTask(taskId, ct, track: true);
        if (task is null)
        {
            return NotFound();
        }

        var siblings = await db.Tasks
            .Where(t => t.BoardId == task.BoardId)
            .OrderBy(t => t.Rank)
            .ToListAsync(ct);
        if (!Ranking.TryApplyMove(siblings, taskId, request.PrecedingId, request.FollowingId,
                t => t.Id, t => t.Rank, (t, r) => t.Rank = r))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid rank target",
                detail: "The neighbor ids are not tasks on this board.");
        }

        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await notifier.BoardEntityChanged(task.BoardId, EntityTypes.Task, taskId,
            ChangeKind.Updated, nameof(task.Rank));
        return task.ToDto();
    }

    [HttpPut("api/tasks/{taskId:guid}/assignees")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<TaskDto>> SetAssignees(
        Guid taskId, SetAssigneesRequest request, CancellationToken ct)
    {
        var task = await LoadTask(taskId, ct, track: true);
        if (task is null)
        {
            return NotFound();
        }

        var invalid = await AssignmentHelper.InvalidAssignees(db, ProjectId(), request.UserIds, ct);
        if (invalid.Count > 0)
        {
            return AssignmentHelper.InvalidAssigneesProblem(this, invalid);
        }

        AssignmentHelper.Sync(db, task.Assignments, request.UserIds, userId =>
            new Assignment { UserId = userId, TaskId = taskId });
        await db.SaveChangesAsync(ct);
        await notifier.BoardEntityChanged(task.BoardId, EntityTypes.Task, taskId,
            ChangeKind.Updated, "Assignees");
        return task.ToDto();
    }

    private Guid ProjectId() => (Guid)HttpContext.Items[ProjectAccessHandler.ProjectIdItem]!;

    private async Task<TaskItem?> LoadTask(Guid taskId, CancellationToken ct, bool track)
    {
        var query = db.Tasks
            .Include(t => t.SubTasks)
            .Include(t => t.Assignments)
            .AsQueryable();
        if (!track)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(t => t.Id == taskId, ct);
    }

    private async Task<ObjectResult?> ValidateLabel(
        Guid? labelId, Guid projectId, Guid boardId, LabelKind kind, CancellationToken ct)
    {
        if (labelId is null)
        {
            return null;
        }

        var label = await db.Labels.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == labelId && l.ProjectId == projectId, ct);
        if (label is null || !LabelScope.IsAssignable(label, boardId, kind))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid label",
                detail: "The label must exist in this project, match the item kind, and be visible on this board.");
        }

        return null;
    }

    private ObjectResult EpicNotOnBoardProblem() => Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Epic not on board",
        detail: "A task's epic must belong to the same board.");
}
