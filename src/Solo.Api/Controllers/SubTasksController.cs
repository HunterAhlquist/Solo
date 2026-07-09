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

public sealed class SubTasksController(SoloDbContext db, IBoardNotifier notifier) : ApiControllerBase
{
    [HttpGet("api/tasks/{taskId:guid}/subtasks")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<SubTaskDto>>> List(Guid taskId, CancellationToken ct)
    {
        var subTasks = await db.SubTasks.AsNoTracking()
            .Where(s => s.TaskId == taskId)
            .OrderBy(s => s.Rank)
            .ToListAsync(ct);
        return subTasks.Select(s => s.ToDto()).ToList();
    }

    [HttpPost("api/tasks/{taskId:guid}/subtasks")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<SubTaskDto>> Create(
        Guid taskId, CreateSubTaskRequest request, CancellationToken ct)
    {
        var task = await db.Tasks.Include(t => t.SubTasks)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null)
        {
            return NotFound();
        }

        var labelProblem = await ValidateLabel(request.LabelId, task, ct);
        if (labelProblem is not null)
        {
            return labelProblem;
        }

        var subTask = new SubTask
        {
            TaskId = taskId,
            Name = request.Name,
            LabelId = request.LabelId,
            Rank = Ranking.ForAppend(task.SubTasks.Select(s => s.Rank)),
        };
        // Explicit Add: ids are client-generated, so an entity discovered only
        // via the tracked parent's navigation would be treated as an existing
        // row (UPDATE) instead of an insert.
        db.SubTasks.Add(subTask);
        task.SubTasks.Add(subTask);
        await db.SaveChangesAsync(ct);

        await notifier.BoardEntityChanged(task.BoardId, EntityTypes.SubTask, subTask.Id, ChangeKind.Created);
        return CreatedAtAction(nameof(List), new { taskId }, subTask.ToDto());
    }

    [HttpPut("api/subtasks/{subTaskId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<SubTaskDto>> Update(
        Guid subTaskId, UpdateSubTaskRequest request, CancellationToken ct)
    {
        var subTask = await LoadWithTask(subTaskId, ct);
        if (subTask is null)
        {
            return NotFound();
        }

        var labelProblem = await ValidateLabel(request.LabelId, subTask.Task!, ct);
        if (labelProblem is not null)
        {
            return labelProblem;
        }

        subTask.Name = request.Name;
        subTask.LabelId = request.LabelId;
        UseClientVersion(db, subTask, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await notifier.BoardEntityChanged(subTask.Task!.BoardId, EntityTypes.SubTask, subTaskId,
            ChangeKind.Updated, nameof(subTask.Name), nameof(subTask.LabelId));
        return subTask.ToDto();
    }

    [HttpDelete("api/subtasks/{subTaskId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<IActionResult> Delete(Guid subTaskId, CancellationToken ct)
    {
        var subTask = await LoadWithTask(subTaskId, ct);
        if (subTask is null)
        {
            return NotFound();
        }

        var task = subTask.Task!;
        task.SubTasks.Remove(subTask);
        db.SubTasks.Remove(subTask);
        await db.SaveChangesAsync(ct);

        await notifier.BoardEntityChanged(task.BoardId, EntityTypes.SubTask, subTaskId, ChangeKind.Deleted);
        return NoContent();
    }

    /// <summary>
    /// Checklist toggle. Persists the tick only; the parent task's column is set
    /// separately (the client auto-moves a fully-checked card to Finished).
    /// </summary>
    [HttpPut("api/subtasks/{subTaskId:guid}/finished")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<SubTaskDto>> SetFinished(
        Guid subTaskId, SetSubTaskFinishedRequest request, CancellationToken ct)
    {
        var subTask = await LoadWithTask(subTaskId, ct);
        if (subTask is null)
        {
            return NotFound();
        }

        subTask.IsFinished = request.IsFinished;
        await db.SaveChangesAsync(ct);

        await notifier.BoardEntityChanged(subTask.Task!.BoardId, EntityTypes.SubTask, subTaskId,
            ChangeKind.Updated, nameof(subTask.IsFinished));
        return subTask.ToDto();
    }

    [HttpPut("api/subtasks/{subTaskId:guid}/rank")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<SubTaskDto>> Rerank(
        Guid subTaskId, RerankRequest request, CancellationToken ct)
    {
        var subTask = await LoadWithTask(subTaskId, ct);
        if (subTask is null)
        {
            return NotFound();
        }

        var siblings = await db.SubTasks
            .Where(s => s.TaskId == subTask.TaskId)
            .OrderBy(s => s.Rank)
            .ToListAsync(ct);
        if (!Ranking.TryApplyMove(siblings, subTaskId, request.PrecedingId, request.FollowingId,
                s => s.Id, s => s.Rank, (s, r) => s.Rank = r))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid rank target",
                detail: "The neighbor ids are not sub-tasks of this task.");
        }

        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await notifier.BoardEntityChanged(subTask.Task!.BoardId, EntityTypes.SubTask, subTaskId,
            ChangeKind.Updated, nameof(subTask.Rank));
        return subTask.ToDto();
    }

    private Task<SubTask?> LoadWithTask(Guid subTaskId, CancellationToken ct) =>
        db.SubTasks
            .Include(s => s.Task).ThenInclude(t => t!.SubTasks)
            .FirstOrDefaultAsync(s => s.Id == subTaskId, ct);

    private async Task<ObjectResult?> ValidateLabel(Guid? labelId, TaskItem task, CancellationToken ct)
    {
        if (labelId is null)
        {
            return null;
        }

        var projectId = await db.Boards.AsNoTracking()
            .Where(b => b.Id == task.BoardId).Select(b => b.ProjectId).FirstAsync(ct);
        var label = await db.Labels.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == labelId && l.ProjectId == projectId, ct);
        if (label is null || !LabelScope.IsAssignable(label, task.BoardId, LabelKind.SubTask))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid label",
                detail: "The label must exist in this project, be a sub-task label, and be visible on this board.");
        }

        return null;
    }
}
