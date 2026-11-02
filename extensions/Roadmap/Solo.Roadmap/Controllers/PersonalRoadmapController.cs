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
/// A person's own roadmap, at <c>/api/ext/roadmap/me</c>: every task assigned
/// to them, from every project they're in, on rows of their own.
/// </summary>
/// <remarks>
/// <para>No project in the route, so core's project policies can't apply: a plain
/// <c>[Authorize]</c>, and every read scoped to the caller through
/// <see cref="ISoloQuery"/>, as the contract requires. Every row here belongs
/// to the caller, and each write checks that it does.</para>
///
/// <para>Nothing here says which tasks are on the roadmap. That is whatever core
/// says is assigned to them, read fresh each time; this package keeps only
/// where each one goes — its row, and the days they planned it for when those
/// aren't the task's own. A drag here never writes a task's dates: the plan is
/// theirs, the deadline is the team's, and the page shows where the two part.</para>
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
    /// The timeline: the caller's assigned tasks in their own row order. A task
    /// assigned since the last read gets a row at the end, on
    /// <paramref name="today"/> (the browser's, where the page draws it; the
    /// server's when none is sent); one no longer assigned, or gone, loses its
    /// row. Milestones are a project's, so there are none.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<RoadmapTimelineDto>> Get(DateOnly? today, CancellationToken ct)
    {
        var caller = await query.CallerAsync(ct);
        var assigned = await query.AssignedTasksAsync(ct);
        var boards = assigned.Boards.ToDictionary(b => b.Id);
        var projects = assigned.Projects.ToDictionary(p => p.Id);
        var tasks = assigned.Tasks
            .Where(t => boards.ContainsKey(t.BoardId) && projects.ContainsKey(boards[t.BoardId].ProjectId))
            .ToDictionary(t => t.Id);

        var entries = await ReconcileAsync(caller.UserId, tasks, boards, projects,
            today ?? DateOnly.FromDateTime(DateTime.UtcNow), ct);

        return new RoadmapTimelineDto(
            Guid.Empty,
            Guid.Empty,
            string.Empty,
            Title,
            0,
            (await settings.GetAsync(ct)).ToDto(),
            entries.Select(e => ToDto(e, tasks[e.TaskId], boards, projects)).ToList(),
            []);
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

    /// <summary>
    /// The caller's rows, brought into line with their assignments: rows for
    /// tasks no longer theirs go, tasks without one get a row each at the end,
    /// grouped by project and board in the order the sidebar lists them. Two
    /// reads racing to add the same row leave one of them with a unique
    /// violation, which is answered by reading what the other wrote.
    /// </summary>
    private async Task<List<PersonalEntry>> ReconcileAsync(
        Guid userId,
        Dictionary<Guid, TaskDto> tasks,
        Dictionary<Guid, BoardDto> boards,
        Dictionary<Guid, ProjectSummaryDto> projects,
        DateOnly today,
        CancellationToken ct)
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

        var held = entries.Select(e => e.TaskId).ToHashSet();
        var rank = entries.Count == 0 ? 0 : entries.Max(e => e.Rank);
        var added = tasks.Values
            .Where(t => !held.Contains(t.Id))
            .OrderBy(t => projects[boards[t.BoardId].ProjectId].Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => boards[t.BoardId].Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Number)
            .Select(t => new PersonalEntry
            {
                UserId = userId,
                TaskId = t.Id,
                Rank = rank += RoadmapRanking.Gap,
                AnchorDay = today,
            })
            .ToList();
        if (added.Count == 0)
        {
            return entries;
        }

        db.PersonalEntries.AddRange(added);
        try
        {
            await db.SaveChangesAsync(ct);
            entries.AddRange(added);
            return entries;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
                                              { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            var taskIds = tasks.Keys.ToList();
            return await db.PersonalEntries.AsNoTracking()
                .Where(e => e.UserId == userId && taskIds.Contains(e.TaskId))
                .OrderBy(e => e.Rank)
                .ThenBy(e => e.Id)
                .ToListAsync(ct);
        }
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

    private static RoadmapEntryDto ToDto(
        PersonalEntry entry,
        TaskDto task,
        Dictionary<Guid, BoardDto> boards,
        Dictionary<Guid, ProjectSummaryDto> projects)
    {
        var board = boards[task.BoardId];
        var project = projects[board.ProjectId];
        return new RoadmapEntryDto(entry.Id, entry.Rank, entry.AnchorDay, board.Alias, board.Name, task,
            project.Alias, project.Name, entry.Schedule);
    }
}
