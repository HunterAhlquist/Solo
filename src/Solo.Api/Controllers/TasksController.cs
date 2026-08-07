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
            .AsSplitQuery()
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
            DeadlineAt = request.DeadlineAt,
            Rank = Ranking.ForAppend(existingRanks),
        };
        db.Tasks.Add(task);
        task.Number = await NextTaskNumberAsync(boardId, ct);
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
        task.DeadlineAt = request.DeadlineAt;
        UseClientVersion(db, task, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await notifier.BoardEntityChanged(task.BoardId, EntityTypes.Task, taskId,
            ChangeKind.Updated, nameof(task.Name), nameof(task.LabelId), nameof(task.Description),
            nameof(task.DeadlineAt));
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

    /// <summary>
    /// Column move. Any status to any column is allowed, sub-tasks or not — the
    /// checklist is a convenience that auto-completes the card client-side, not a
    /// lock on where the user may place it.
    /// </summary>
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

    /// <summary>
    /// Board transfer, within the project. The task keeps its identity and
    /// everything hanging off it — checklist, comments, assignees, audit trail.
    /// What was scoped to the old board is re-resolved against the new one: a
    /// fresh serial number (the old one belongs to the old board's sequence), a
    /// rank at the end of the destination, and labels remapped to that board's
    /// own copies.
    /// </summary>
    [HttpPut("api/tasks/{taskId:guid}/board")]
    [Authorize(Policy = AuthConstants.ProjectWritePolicy)]
    public async Task<ActionResult<TaskDto>> Move(
        Guid taskId, MoveTaskRequest request, CancellationToken ct)
    {
        var task = await LoadTask(taskId, ct, track: true);
        if (task is null)
        {
            return NotFound();
        }

        if (request.BoardId == task.BoardId)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Already on this board",
                detail: "Pick a different board; the epic and status moves cover this one.");
        }

        // Same project only. The route authorized the *source* project, so this
        // is the access check as much as the scope rule — which is why a board
        // that doesn't exist and one the caller may not even see answer alike.
        var target = await db.Boards.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == request.BoardId && b.ProjectId == ProjectId(), ct);
        if (target is null)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid destination board",
                detail: "The destination must be another board in the same project.");
        }

        if (request.EpicId is not null &&
            !await db.Epics.AsNoTracking()
                .AnyAsync(e => e.Id == request.EpicId && e.BoardId == target.Id, ct))
        {
            return EpicNotOnBoardProblem();
        }

        var sourceBoardId = task.BoardId;
        var source = await db.Boards.AsNoTracking()
            .Where(b => b.Id == sourceBoardId)
            .Select(b => new { b.Alias, ProjectAlias = b.Project!.Alias })
            .FirstAsync(ct);
        var oldSerial = TaskReferences.Reference.Of(source.ProjectAlias, source.Alias, task.Number);

        await RemapLabelsAsync(task, target.Id, ct);

        var ranks = await db.Tasks.AsNoTracking()
            .Where(t => t.BoardId == target.Id).Select(t => t.Rank).ToListAsync(ct);
        task.BoardId = target.Id;
        task.EpicId = request.EpicId;
        task.Rank = Ranking.ForAppend(ranks);
        task.Number = await NextTaskNumberAsync(target.Id, ct);

        var rewritten = await RetargetReferencesAsync(
            target.ProjectId, oldSerial,
            TaskReferences.Reference.Of(source.ProjectAlias, target.Alias, task.Number), ct);

        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        // Two boards change at once, and a card leaving one for the other is
        // exactly what a delete + create pair says to each board's group.
        await notifier.BoardEntityChanged(sourceBoardId, EntityTypes.Task, taskId, ChangeKind.Deleted);
        await notifier.BoardEntityChanged(target.Id, EntityTypes.Task, taskId, ChangeKind.Created);

        // Rewritten prose can sit on any board in the project, so anyone reading
        // one refetches rather than being left on text that no longer exists.
        if (rewritten > 0)
        {
            await notifier.ProjectEntityChanged(
                target.ProjectId, EntityTypes.Board, target.ProjectId, ChangeKind.Updated);
        }

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
        // Sub-tasks and assignees are unrelated to each other, so a single join
        // would repeat the task's own row — description text and all — once per
        // pairing of the two.
        var query = db.Tasks
            .Include(t => t.SubTasks)
            .Include(t => t.Assignments)
            .AsSplitQuery();
        if (!track)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(t => t.Id == taskId, ct);
    }

    /// <summary>
    /// The board's next serial number, taken by bumping its counter atomically,
    /// so concurrent writers get distinct numbers and a deleted task's number is
    /// never reassigned. A failed save burns the number — gaps are fine, reuse isn't.
    /// </summary>
    private async Task<int> NextTaskNumberAsync(Guid boardId, CancellationToken ct)
    {
        var numbers = await db.Database.SqlQuery<int>(
            $"""
             WITH bumped AS (
                 UPDATE "Boards" SET "NextTaskNumber" = "NextTaskNumber" + 1
                 WHERE "Id" = {boardId}
                 RETURNING "NextTaskNumber" - 1 AS "Value"
             )
             SELECT "Value" FROM bumped
             """).ToListAsync(ct);
        return numbers.Single();
    }

    /// <summary>
    /// A task's serial number is how prose refers to it — descriptions and
    /// comments carry <c>||PROJECT-BOARD-N||</c>. A board transfer changes both
    /// halves of that, so the references follow the task in the same transaction
    /// rather than rotting into links that quietly resolve to nothing. Returns
    /// how many rows the rewrite touched.
    ///
    /// Scoped to the task's own project: that is where the references realistically
    /// live, it bounds the read, and it keeps the edit inside what the caller is
    /// authorized to write.
    /// </summary>
    private async Task<int> RetargetReferencesAsync(
        Guid projectId, TaskReferences.Reference from, TaskReferences.Reference to, CancellationToken ct)
    {
        // Only text carrying the delimiters can hold a reference; the rewrite
        // itself is a regex, so this filter is here purely to keep the read small.
        var tasks = await db.Tasks
            .Where(t => t.Board!.ProjectId == projectId &&
                        t.Description != null && t.Description.Contains(TaskReferences.Delimiter))
            .ToListAsync(ct);
        var epics = await db.Epics
            .Where(e => e.Board!.ProjectId == projectId &&
                        e.Description != null && e.Description.Contains(TaskReferences.Delimiter))
            .ToListAsync(ct);
        var comments = await db.Comments
            .Where(c => (c.TaskId != null ? c.Task!.Board!.ProjectId : c.Epic!.Board!.ProjectId) == projectId &&
                        c.Body.Contains(TaskReferences.Delimiter))
            .ToListAsync(ct);

        // Text that held no reference to this task comes back identical, and is
        // left untouched rather than re-assigned — so the save, the audit trail
        // and this count all cover only what actually moved.
        var rewritten = 0;
        rewritten += Apply(tasks, t => t.Description!, (t, text) => t.Description = text);
        rewritten += Apply(epics, e => e.Description!, (e, text) => e.Description = text);
        rewritten += Apply(comments, c => c.Body, (c, text) => c.Body = text);
        return rewritten;

        int Apply<T>(List<T> rows, Func<T, string> read, Action<T, string> write)
        {
            var changed = 0;
            foreach (var row in rows)
            {
                var text = read(row);
                var updated = TaskReferences.Retarget(text, from, to);
                if (updated == text)
                {
                    continue;
                }

                write(row, updated);
                changed++;
            }

            return changed;
        }
    }

    /// <summary>
    /// Labels belong to a board, so a transferred task cannot keep pointing at
    /// the board it left — its own and its sub-tasks' are re-resolved to the
    /// destination's equivalents, and cleared where it has none.
    /// </summary>
    private async Task RemapLabelsAsync(TaskItem task, Guid targetBoardId, CancellationToken ct)
    {
        var labelIds = task.SubTasks.Select(s => s.LabelId).Append(task.LabelId)
            .OfType<Guid>().Distinct().ToList();
        if (labelIds.Count == 0)
        {
            return;
        }

        var current = await db.Labels.AsNoTracking()
            .Where(l => labelIds.Contains(l.Id)).ToListAsync(ct);
        var candidates = await db.Labels.AsNoTracking()
            .Where(l => l.BoardId == targetBoardId).ToListAsync(ct);
        var remapped = current.ToDictionary(l => l.Id, l => BoardLabels.Counterpart(l, candidates)?.Id);

        task.LabelId = Remap(task.LabelId);
        foreach (var subTask in task.SubTasks)
        {
            subTask.LabelId = Remap(subTask.LabelId);
        }

        Guid? Remap(Guid? labelId) =>
            labelId is { } id && remapped.TryGetValue(id, out var match) ? match : null;
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
                detail: "The label must belong to this board and match the item kind.");
        }

        return null;
    }

    private ObjectResult EpicNotOnBoardProblem() => Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Epic not on board",
        detail: "A task's epic must belong to the same board.");
}
