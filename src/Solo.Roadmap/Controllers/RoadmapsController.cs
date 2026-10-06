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
/// Roadmaps and their entries, at <c>/api/ext/roadmap/projects/{projectId}/roadmaps</c>.
/// </summary>
/// <remarks>
/// <para>Every route carries the project, so core's project policies decide
/// access. A roadmap id is only honoured under its own project's route — each
/// action checks <c>ProjectId</c> and answers 404 otherwise — so access to one
/// project can't be used to read or edit another's roadmaps.</para>
///
/// <para>Core is read through <see cref="ISoloQuery"/> only, and never written:
/// a task's dates are the client's to set, through core's schedule endpoint.</para>
///
/// <para>Every change is announced on its live topic once it's saved (see
/// <see cref="RoadmapTopics"/>), so other people's open pages follow it.</para>
/// </remarks>
[ApiController]
[Route("projects/{projectId:guid}/roadmaps")]
public sealed class RoadmapsController(
    RoadmapDbContext db, ISoloQuery query, RoadmapSettingsStore settings, ISoloLiveUpdates live)
    : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<IReadOnlyList<RoadmapSummaryDto>> List(Guid projectId, CancellationToken ct) =>
        await db.Roadmaps.AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.Title)
            .Select(r => new RoadmapSummaryDto(r.Id, r.Title, r.Version))
            .ToListAsync(ct);

    [HttpPost]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<RoadmapSummaryDto>> Create(
        Guid projectId, CreateRoadmapRequest request, CancellationToken ct)
    {
        var caller = await query.CallerAsync(ct);
        var roadmap = new Data.Roadmap
        {
            ProjectId = projectId,
            Title = request.Title.Trim(),
            CreatedByUserId = caller.UserId,
        };
        db.Roadmaps.Add(roadmap);
        await db.SaveChangesAsync(ct);
        await live.PublishAsync(projectId, RoadmapTopics.List, RoadmapTopics.ListChanged, ct: ct);

        return CreatedAtAction(nameof(Get), new { projectId, roadmapId = roadmap.Id },
            new RoadmapSummaryDto(roadmap.Id, roadmap.Title, roadmap.Version));
    }

    [HttpPut("{roadmapId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<RoadmapSummaryDto>> Rename(
        Guid projectId, Guid roadmapId, RenameRoadmapRequest request, CancellationToken ct)
    {
        var roadmap = await db.Roadmaps.FirstOrDefaultAsync(r => r.Id == roadmapId && r.ProjectId == projectId, ct);
        if (roadmap is null)
        {
            return NotFound();
        }

        roadmap.Title = request.Title.Trim();
        db.Entry(roadmap).Property(r => r.Version).OriginalValue = request.Version;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChangedElsewhere();
        }

        await live.PublishAsync(projectId, RoadmapTopics.List, RoadmapTopics.ListChanged, ct: ct);
        await live.PublishAsync(projectId, RoadmapTopics.Roadmap(roadmapId), RoadmapTopics.Renamed, ct: ct);
        return new RoadmapSummaryDto(roadmap.Id, roadmap.Title, roadmap.Version);
    }

    /// <summary>The roadmap and its entries. The tasks themselves are untouched.</summary>
    [HttpDelete("{roadmapId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> Delete(Guid projectId, Guid roadmapId, CancellationToken ct)
    {
        var deleted = await db.Roadmaps
            .Where(r => r.Id == roadmapId && r.ProjectId == projectId)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return NotFound();
        }

        await live.PublishAsync(projectId, RoadmapTopics.List, RoadmapTopics.ListChanged, ct: ct);
        await live.PublishAsync(projectId, RoadmapTopics.Roadmap(roadmapId), RoadmapTopics.Deleted, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// The timeline: title, bounds, and every entry in roadmap order with its
    /// task. One board read and one task read per board, joined here — never a
    /// read per entry.
    /// </summary>
    [HttpGet("{roadmapId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<ActionResult<RoadmapTimelineDto>> Get(Guid projectId, Guid roadmapId, CancellationToken ct)
    {
        var roadmap = await db.Roadmaps.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roadmapId && r.ProjectId == projectId, ct);
        if (roadmap is null)
        {
            return NotFound();
        }

        var tasks = await ProjectTasksAsync(projectId, ct);
        var entries = await db.Entries.AsNoTracking()
            .Where(e => e.RoadmapId == roadmapId)
            .OrderBy(e => e.Rank)
            .ToListAsync(ct);

        // Every task in the project is readable to anyone who passed the policy,
        // so an entry whose task isn't in the join points at a deleted task. The
        // delete handler normally got there first; this catches what it missed.
        var gone = entries.Where(e => !tasks.ContainsKey(e.TaskId)).Select(e => e.Id).ToList();
        if (gone.Count > 0)
        {
            await db.Entries.Where(e => gone.Contains(e.Id)).ExecuteDeleteAsync(ct);
        }

        var project = (await query.ProjectsAsync(ct)).FirstOrDefault(p => p.Id == projectId);
        return new RoadmapTimelineDto(
            roadmap.Id,
            projectId,
            project?.Alias ?? string.Empty,
            roadmap.Title,
            roadmap.Version,
            (await settings.GetAsync(ct)).ToDto(),
            entries.Where(e => tasks.ContainsKey(e.TaskId))
                .Select(e => ToDto(e, tasks[e.TaskId]))
                .ToList());
    }

    /// <summary>Puts a task on the roadmap, at the end.</summary>
    [HttpPost("{roadmapId:guid}/entries")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<RoadmapEntryDto>> AddEntry(
        Guid projectId, Guid roadmapId, AddRoadmapEntryRequest request, CancellationToken ct)
    {
        if (!await db.Roadmaps.AnyAsync(r => r.Id == roadmapId && r.ProjectId == projectId, ct))
        {
            return NotFound();
        }

        var task = await query.TaskAsync(request.TaskId, ct);
        var board = task is null
            ? null
            : (await query.BoardsAsync(projectId, ct)).FirstOrDefault(b => b.Id == task.BoardId);
        if (task is null || board is null)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Task not in this project",
                detail: "Only a task on one of this project's boards can go on its roadmap.");
        }

        if (request.AnchorDay == default)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "No day given",
                detail: "Say which day the task is being added on.");
        }

        var ranks = await db.Entries.Where(e => e.RoadmapId == roadmapId).Select(e => e.Rank).ToListAsync(ct);
        var entry = new RoadmapEntry
        {
            RoadmapId = roadmapId,
            TaskId = task.Id,
            Rank = RoadmapRanking.ForAppend(ranks),
            AnchorDay = request.AnchorDay,
        };
        db.Entries.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
                                              { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Already on this roadmap",
                detail: "That task is already on this roadmap.");
        }

        await EntriesChangedAsync(projectId, roadmapId, ct);
        return Created((string?)null, ToDto(entry, (task, board)));
    }

    /// <summary>Reorders a row on this roadmap only; the task's board rank doesn't move.</summary>
    [HttpPut("{roadmapId:guid}/entries/{entryId:guid}/rank")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> Rerank(
        Guid projectId, Guid roadmapId, Guid entryId, RerankRoadmapEntryRequest request, CancellationToken ct)
    {
        if (!await db.Roadmaps.AnyAsync(r => r.Id == roadmapId && r.ProjectId == projectId, ct))
        {
            return NotFound();
        }

        var entries = await db.Entries
            .Where(e => e.RoadmapId == roadmapId)
            .OrderBy(e => e.Rank)
            .ToListAsync(ct);
        if (entries.All(e => e.Id != entryId))
        {
            return NotFound();
        }

        if (!RoadmapRanking.TryApplyMove(entries, entryId, request.PrecedingId, request.FollowingId,
                e => e.Id, e => e.Rank, (e, rank) => e.Rank = rank))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid rank target",
                detail: "The neighbor ids are not entries on this roadmap.");
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChangedElsewhere();
        }

        await EntriesChangedAsync(projectId, roadmapId, ct);
        return NoContent();
    }

    /// <summary>
    /// Moves the day a task with no deadline is drawn on. A task with dates is
    /// moved by rescheduling it through core; this is only for the pill, whose
    /// day is the roadmap's to keep.
    /// </summary>
    [HttpPut("{roadmapId:guid}/entries/{entryId:guid}/anchor")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> SetAnchor(
        Guid projectId, Guid roadmapId, Guid entryId, SetRoadmapEntryAnchorRequest request, CancellationToken ct)
    {
        if (request.AnchorDay == default)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "No day given",
                detail: "Say which day the task should be drawn on.");
        }

        var updated = await db.Entries
            .Where(e => e.Id == entryId && e.RoadmapId == roadmapId && e.Roadmap.ProjectId == projectId)
            .ExecuteUpdateAsync(set => set.SetProperty(e => e.AnchorDay, request.AnchorDay), ct);
        if (updated == 0)
        {
            return NotFound();
        }

        await EntriesChangedAsync(projectId, roadmapId, ct);
        return NoContent();
    }

    /// <summary>Takes a task off the roadmap. The task itself is untouched.</summary>
    [HttpDelete("{roadmapId:guid}/entries/{entryId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> RemoveEntry(Guid projectId, Guid roadmapId, Guid entryId, CancellationToken ct)
    {
        var deleted = await db.Entries
            .Where(e => e.Id == entryId && e.RoadmapId == roadmapId && e.Roadmap.ProjectId == projectId)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return NotFound();
        }

        await EntriesChangedAsync(projectId, roadmapId, ct);
        return NoContent();
    }

    private Task EntriesChangedAsync(Guid projectId, Guid roadmapId, CancellationToken ct) =>
        live.PublishAsync(projectId, RoadmapTopics.Roadmap(roadmapId), RoadmapTopics.EntriesChanged, ct: ct);

    /// <summary>Every task in the project with the board it sits on, keyed by task id.</summary>
    private async Task<Dictionary<Guid, (TaskDto Task, BoardDto Board)>> ProjectTasksAsync(
        Guid projectId, CancellationToken ct)
    {
        var tasks = new Dictionary<Guid, (TaskDto, BoardDto)>();
        foreach (var board in await query.BoardsAsync(projectId, ct))
        {
            foreach (var task in await query.TasksAsync(board.Id, ct))
            {
                tasks[task.Id] = (task, board);
            }
        }

        return tasks;
    }

    private static RoadmapEntryDto ToDto(RoadmapEntry entry, (TaskDto Task, BoardDto Board) placed) =>
        new(entry.Id, entry.Rank, entry.AnchorDay, placed.Board.Alias, placed.Board.Name, placed.Task);

    private ObjectResult ChangedElsewhere() => Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Concurrency conflict",
        detail: "The roadmap was changed by someone else. Re-fetch and try again.");
}
