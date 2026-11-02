using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solo.Extensibility;
using Solo.Roadmap.Contracts;
using Solo.Roadmap.Data;
using Solo.Shared.Dtos;

namespace Solo.Roadmap.Controllers;

/// <summary>
/// A person's own roadmap, at <c>/api/ext/roadmap/me</c>: the tasks assigned to
/// them that they chose to put on it, from any project they're in, on rows of
/// their own.
/// </summary>
/// <remarks>
/// <para>No project in the route, so core's project policies can't apply: a plain
/// <c>[Authorize]</c>, and every read scoped to the caller through
/// <see cref="ISoloQuery"/>, as the contract requires. Every row here belongs
/// to the caller, and each write checks that it does.</para>
///
/// <para>Nothing is put on it for them: each row is one they added, and only a
/// task core says is assigned to them can be added. Whether a row may stay is
/// asked again on every read, so a task handed to someone else or deleted
/// leaves on its own. This package keeps only where each one goes — its row,
/// and the days they planned it for when those aren't the task's own. A drag
/// here never writes a task's dates: the plan is theirs, the deadline is the
/// team's, and the page shows where the two part.</para>
/// </remarks>
[ApiController]
[Route("me")]
[Authorize]
public sealed class PersonalRoadmapController(
    RoadmapDbContext db, ISoloQuery query, RoadmapSettingsStore settings, ISoloLiveUpdates live)
    : ControllerBase
{
    public const string Title = "Your Roadmap";

    /// <summary>
    /// The timeline: the caller's rows in their own order, less any whose task
    /// is no longer assigned to them, or gone, which are dropped as they're
    /// found. Milestones are a project's, so there are none.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<RoadmapTimelineDto>> Get(CancellationToken ct)
    {
        var caller = await query.CallerAsync(ct);
        var assigned = await query.AssignedTasksAsync(ct);
        var boards = assigned.Boards.ToDictionary(b => b.Id);
        var projects = assigned.Projects.ToDictionary(p => p.Id);
        var tasks = assigned.Tasks
            .Where(t => boards.ContainsKey(t.BoardId) && projects.ContainsKey(boards[t.BoardId].ProjectId))
            .ToDictionary(t => t.Id);

        var entries = await HeldAsync(caller.UserId, tasks, ct);

        return new RoadmapTimelineDto(
            Guid.Empty,
            Guid.Empty,
            string.Empty,
            Title,
            0,
            (await settings.GetAsync(ct)).ToDto(),
            entries.Select(e =>
            {
                var task = tasks[e.TaskId];
                var board = boards[task.BoardId];
                return ToDto(e, task, board, projects[board.ProjectId]);
            }).ToList(),
            []);
    }

    /// <summary>
    /// Puts one of the caller's assigned tasks on their roadmap, beside
    /// <see cref="AddRoadmapEntryRequest.JoinId"/> or on a row of its own at the
    /// end. A task with no deadline would be a one-day pill too narrow to name
    /// it, so it starts with a plan of its own from the day it was added on
    /// (<see cref="RoadmapScheduleDto.OnAdd"/>); one with a deadline follows the
    /// task's dates until it's dragged.
    /// </summary>
    [HttpPost("entries")]
    public async Task<ActionResult<RoadmapEntryDto>> AddEntry(AddRoadmapEntryRequest request, CancellationToken ct)
    {
        if (request.AnchorDay == default)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "No day given",
                detail: "Say which day the task is being added on.");
        }

        var caller = await query.CallerAsync(ct);
        var assigned = await query.AssignedTasksAsync(ct);
        var task = assigned.Tasks.FirstOrDefault(t => t.Id == request.TaskId);
        var board = task is null ? null : assigned.Boards.FirstOrDefault(b => b.Id == task.BoardId);
        var project = board is null ? null : assigned.Projects.FirstOrDefault(p => p.Id == board.ProjectId);
        if (task is null || board is null || project is null)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Not one of your tasks",
                detail: "Only a task assigned to you, on an open board, can go on your roadmap.");
        }

        var held = await db.PersonalEntries
            .Where(e => e.UserId == caller.UserId)
            .Select(e => new { e.Id, e.Rank })
            .ToListAsync(ct);
        var rank = held.FirstOrDefault(e => e.Id == request.JoinId)?.Rank
                   ?? RoadmapRanking.ForAppend(held.Select(e => e.Rank));
        var plan = task.DeadlineAt is null ? RoadmapScheduleDto.OnAdd(request.AnchorDay) : null;
        var entry = new PersonalEntry
        {
            UserId = caller.UserId,
            TaskId = task.Id,
            Rank = rank,
            AnchorDay = request.AnchorDay,
            StartAt = plan?.Start,
            EndAt = plan?.End,
        };
        db.PersonalEntries.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
                                              { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Already on your roadmap",
                detail: "That task is already on your roadmap.");
        }

        await ChangedAsync(caller.UserId, ct);
        return Created((string?)null, ToDto(entry, task, board, project));
    }

    /// <summary>Takes tasks off the caller's roadmap. The tasks themselves, and their assignments, stay as they are.</summary>
    [HttpPost("entries/remove")]
    public async Task<IActionResult> RemoveEntries(RemoveRoadmapEntriesRequest request, CancellationToken ct)
    {
        var caller = await query.CallerAsync(ct);
        var ids = request.EntryIds.Distinct().ToList();
        var deleted = await db.PersonalEntries
            .Where(e => e.UserId == caller.UserId && ids.Contains(e.Id))
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
        {
            await ChangedAsync(caller.UserId, ct);
        }

        return NoContent();
    }

    /// <summary>Moves a task to a row of its own between two others, or onto another's row.</summary>
    [HttpPut("entries/{entryId:guid}/rank")]
    public async Task<IActionResult> Rerank(Guid entryId, RerankRoadmapEntryRequest request, CancellationToken ct)
    {
        if (TwoTargets(request.JoinId, request.PrecedingId, request.FollowingId) is { } twice)
        {
            return twice;
        }

        var caller = await query.CallerAsync(ct);
        var entries = await db.PersonalEntries
            .Where(e => e.UserId == caller.UserId)
            .OrderBy(e => e.Rank)
            .ToListAsync(ct);
        if (entries.All(e => e.Id != entryId))
        {
            return NotFound();
        }

        var moved = request.JoinId is { } joinId
            ? RoadmapRanking.TryJoin(entries, [entryId], joinId, e => e.Id, e => e.Rank, (e, rank) => e.Rank = rank)
            : RoadmapRanking.TryApplyMove(entries, entryId, request.PrecedingId, request.FollowingId,
                e => e.Id, e => e.Rank, (e, rank) => e.Rank = rank);
        if (!moved)
        {
            return InvalidTarget();
        }

        return await SaveAsync(caller.UserId, ct);
    }

    /// <summary>Moves several tasks to one place, for a selection dragged up or down.</summary>
    [HttpPut("entries/rank")]
    public async Task<IActionResult> RerankMany(RerankRoadmapEntriesRequest request, CancellationToken ct)
    {
        if (TwoTargets(request.JoinId, request.PrecedingId, request.FollowingId) is { } twice)
        {
            return twice;
        }

        var caller = await query.CallerAsync(ct);
        var entries = await db.PersonalEntries
            .Where(e => e.UserId == caller.UserId)
            .OrderBy(e => e.Rank)
            .ToListAsync(ct);
        var moving = entries.Select(e => e.Id).Intersect(request.EntryIds).ToList();
        if (moving.Count == 0)
        {
            return NotFound();
        }

        var moved = request.JoinId is { } joinId
            ? RoadmapRanking.TryJoin(entries, moving, joinId, e => e.Id, e => e.Rank, (e, rank) => e.Rank = rank)
            : RoadmapRanking.TryApplyBlockMove(entries, moving, request.PrecedingId, request.FollowingId,
                e => e.Id, e => e.Rank, (e, rank) => e.Rank = rank);
        if (!moved)
        {
            return InvalidTarget();
        }

        return await SaveAsync(caller.UserId, ct);
    }

    /// <summary>Moves the day a task with no deadline is drawn on.</summary>
    [HttpPut("entries/{entryId:guid}/anchor")]
    public async Task<IActionResult> SetAnchor(Guid entryId, SetRoadmapEntryAnchorRequest request, CancellationToken ct)
    {
        if (request.AnchorDay == default)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "No day given",
                detail: "Say which day the task should be drawn on.");
        }

        var caller = await query.CallerAsync(ct);
        var updated = await db.PersonalEntries
            .Where(e => e.Id == entryId && e.UserId == caller.UserId)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.AnchorDay, request.AnchorDay), ct);
        if (updated == 0)
        {
            return NotFound();
        }

        await ChangedAsync(caller.UserId, ct);
        return NoContent();
    }

    /// <summary>
    /// Where the person plans the task: the bar's days on their roadmap alone.
    /// The task's own dates stay as they are, whoever set them.
    /// </summary>
    [HttpPut("entries/{entryId:guid}/schedule")]
    public async Task<IActionResult> SetSchedule(Guid entryId, SetPersonalScheduleRequest request, CancellationToken ct)
    {
        if (request.Start == default || request.End == default || request.End < request.Start)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid days",
                detail: "Give a start and an end, the end no earlier than the start.");
        }

        var caller = await query.CallerAsync(ct);
        var updated = await db.PersonalEntries
            .Where(e => e.Id == entryId && e.UserId == caller.UserId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(e => e.StartAt, request.Start)
                .SetProperty(e => e.EndAt, request.End), ct);
        if (updated == 0)
        {
            return NotFound();
        }

        await ChangedAsync(caller.UserId, ct);
        return NoContent();
    }

    /// <summary>Puts the bar back on the task's dates, and keeps it following them.</summary>
    [HttpDelete("entries/{entryId:guid}/schedule")]
    public async Task<IActionResult> ClearSchedule(Guid entryId, CancellationToken ct)
    {
        var caller = await query.CallerAsync(ct);
        var updated = await db.PersonalEntries
            .Where(e => e.Id == entryId && e.UserId == caller.UserId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(e => e.StartAt, (DateOnly?)null)
                .SetProperty(e => e.EndAt, (DateOnly?)null), ct);
        if (updated == 0)
        {
            return NotFound();
        }

        await ChangedAsync(caller.UserId, ct);
        return NoContent();
    }

    /// <summary>The caller's rows whose task is still theirs; the rest are deleted.</summary>
    private async Task<List<PersonalEntry>> HeldAsync(
        Guid userId, Dictionary<Guid, TaskDto> tasks, CancellationToken ct)
    {
        var entries = await db.PersonalEntries
            .Where(e => e.UserId == userId)
            .OrderBy(e => e.Rank)
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        var gone = entries.Where(e => !tasks.ContainsKey(e.TaskId)).Select(e => e.Id).ToList();
        if (gone.Count > 0)
        {
            await db.PersonalEntries.Where(e => gone.Contains(e.Id)).ExecuteDeleteAsync(ct);
            entries.RemoveAll(e => gone.Contains(e.Id));
        }

        return entries;
    }

    private async Task<IActionResult> SaveAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Concurrency conflict",
                detail: "Your roadmap was changed from another tab. Re-fetch and try again.");
        }

        await ChangedAsync(userId, ct);
        return NoContent();
    }

    /// <summary>The person's other tabs: the one that made the change has it already.</summary>
    private Task ChangedAsync(Guid userId, CancellationToken ct) =>
        live.PublishToUserAsync(userId, RoadmapTopics.Personal, RoadmapTopics.EntriesChanged, ct: ct);

    private ObjectResult? TwoTargets(Guid? joinId, Guid? precedingId, Guid? followingId) =>
        joinId is not null && (precedingId is not null || followingId is not null)
            ? Problem(statusCode: StatusCodes.Status400BadRequest, title: "Two rank targets",
                detail: "Name a row to join or neighbors to go between, not both.")
            : null;

    private ObjectResult InvalidTarget() => Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid rank target",
        detail: "The neighbor or row ids are not other rows on your roadmap.");

    private static RoadmapEntryDto ToDto(PersonalEntry entry, TaskDto task, BoardDto board, ProjectSummaryDto project) =>
        new(entry.Id, entry.Rank, entry.AnchorDay, board.Alias, board.Name, task, project.Alias, project.Name,
            entry.Schedule);
}
