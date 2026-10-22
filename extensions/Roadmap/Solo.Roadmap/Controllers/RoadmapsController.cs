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
/// Roadmaps, their entries and their milestones, at <c>/api/ext/roadmap/projects/{projectId}/roadmaps</c>.
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

    /// <remarks>
    /// Creating and deleting a roadmap is the super admin's, as a board's is;
    /// what goes on one stays with the project's writers. The owner policy
    /// alongside resolves the project from the route, so a super admin can't
    /// file a roadmap under a project that isn't there.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = SoloPolicies.SuperAdmin)]
    [Authorize(Policy = SoloPolicies.ProjectOwner)]
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

    /// <summary>The roadmap and its entries. The tasks themselves are untouched. Super admin only, as creation is.</summary>
    [HttpDelete("{roadmapId:guid}")]
    [Authorize(Policy = SoloPolicies.SuperAdmin)]
    [Authorize(Policy = SoloPolicies.ProjectOwner)]
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
            .ThenBy(e => e.Id)
            .ToListAsync(ct);

        // Every task in the project is readable to anyone who passed the policy,
        // so an entry whose task isn't in the join points at a deleted task. The
        // delete handler normally got there first; this catches what it missed.
        var gone = entries.Where(e => !tasks.ContainsKey(e.TaskId)).Select(e => e.Id).ToList();
        if (gone.Count > 0)
        {
            await db.Entries.Where(e => gone.Contains(e.Id)).ExecuteDeleteAsync(ct);
        }

        var milestones = await db.Milestones.AsNoTracking()
            .Where(m => m.RoadmapId == roadmapId)
            .OrderBy(m => m.Day)
            .ToListAsync(ct);

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
                .ToList(),
            milestones.Select(m => m.ToDto()).ToList());
    }

    /// <summary>
    /// Puts a task on the roadmap: on the row the request names, or a row of
    /// its own at the end — also when that row's entry has gone meanwhile, since
    /// the task going on the roadmap is what was asked for.
    /// </summary>
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

        var rank = request.JoinId is { } joinId
            ? await db.Entries
                .Where(e => e.Id == joinId && e.RoadmapId == roadmapId)
                .Select(e => (decimal?)e.Rank)
                .FirstOrDefaultAsync(ct)
            : null;
        rank ??= RoadmapRanking.ForAppend(
            await db.Entries.Where(e => e.RoadmapId == roadmapId).Select(e => e.Rank).ToListAsync(ct));
        var entry = new RoadmapEntry
        {
            RoadmapId = roadmapId,
            TaskId = task.Id,
            Rank = rank.Value,
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

    /// <summary>
    /// Moves a task to a row of its own between two others, or onto another's
    /// row, on this roadmap only; the task's board rank doesn't move.
    /// </summary>
    [HttpPut("{roadmapId:guid}/entries/{entryId:guid}/rank")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> Rerank(
        Guid projectId, Guid roadmapId, Guid entryId, RerankRoadmapEntryRequest request, CancellationToken ct)
    {
        if (!await db.Roadmaps.AnyAsync(r => r.Id == roadmapId && r.ProjectId == projectId, ct))
        {
            return NotFound();
        }

        if (TwoTargets(request.JoinId, request.PrecedingId, request.FollowingId) is { } twice)
        {
            return twice;
        }

        var entries = await db.Entries
            .Where(e => e.RoadmapId == roadmapId)
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
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid rank target",
                detail: "The neighbor or row ids are not other entries on this roadmap.");
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
    /// Moves several tasks to one place on this roadmap, for a selection dragged
    /// up or down: they land together between the neighbors, on rows in the
    /// order they already had, or all on one row beside another task. Entries
    /// already taken off by someone else are passed over, as a selection's
    /// Remove does.
    /// </summary>
    [HttpPut("{roadmapId:guid}/entries/rank")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> RerankMany(
        Guid projectId, Guid roadmapId, RerankRoadmapEntriesRequest request, CancellationToken ct)
    {
        if (!await db.Roadmaps.AnyAsync(r => r.Id == roadmapId && r.ProjectId == projectId, ct))
        {
            return NotFound();
        }

        if (TwoTargets(request.JoinId, request.PrecedingId, request.FollowingId) is { } twice)
        {
            return twice;
        }

        var entries = await db.Entries
            .Where(e => e.RoadmapId == roadmapId)
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
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid rank target",
                detail: "The neighbor or row ids are not entries on this roadmap outside the ones being moved.");
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

    /// <summary>
    /// Takes several tasks off the roadmap in one go, for a selection's Remove:
    /// one request and one change announced, however many there are. Entries
    /// already gone, taken off by someone else meanwhile, are skipped rather
    /// than failing the rest. The tasks themselves are untouched.
    /// </summary>
    [HttpPost("{roadmapId:guid}/entries/remove")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> RemoveEntries(
        Guid projectId, Guid roadmapId, RemoveRoadmapEntriesRequest request, CancellationToken ct)
    {
        if (!await db.Roadmaps.AnyAsync(r => r.Id == roadmapId && r.ProjectId == projectId, ct))
        {
            return NotFound();
        }

        var ids = request.EntryIds.Distinct().ToList();
        var deleted = await db.Entries
            .Where(e => e.RoadmapId == roadmapId && ids.Contains(e.Id))
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
        {
            await EntriesChangedAsync(projectId, roadmapId, ct);
        }

        return NoContent();
    }

    // -- Milestones ------------------------------------------------------------

    [HttpPost("{roadmapId:guid}/milestones")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<RoadmapMilestoneDto>> AddMilestone(
        Guid projectId, Guid roadmapId, CreateRoadmapMilestoneRequest request, CancellationToken ct)
    {
        if (!await db.Roadmaps.AnyAsync(r => r.Id == roadmapId && r.ProjectId == projectId, ct))
        {
            return NotFound();
        }

        if (MilestoneProblem(request.Name, request.Day) is { } problem)
        {
            return problem;
        }

        var milestone = new RoadmapMilestone
        {
            RoadmapId = roadmapId,
            Name = request.Name.Trim(),
            Day = request.Day,
            Color = request.Color.ToLowerInvariant(),
        };
        db.Milestones.Add(milestone);
        await db.SaveChangesAsync(ct);

        await MilestonesChangedAsync(projectId, roadmapId, ct);
        return Created((string?)null, milestone.ToDto());
    }

    /// <summary>Renames, recolours or moves a milestone; a drag of its line sends the rest unchanged.</summary>
    [HttpPut("{roadmapId:guid}/milestones/{milestoneId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<RoadmapMilestoneDto>> UpdateMilestone(
        Guid projectId, Guid roadmapId, Guid milestoneId, UpdateRoadmapMilestoneRequest request, CancellationToken ct)
    {
        var milestone = await db.Milestones.FirstOrDefaultAsync(
            m => m.Id == milestoneId && m.RoadmapId == roadmapId && m.Roadmap.ProjectId == projectId, ct);
        if (milestone is null)
        {
            return NotFound();
        }

        if (MilestoneProblem(request.Name, request.Day) is { } problem)
        {
            return problem;
        }

        milestone.Name = request.Name.Trim();
        milestone.Day = request.Day;
        milestone.Color = request.Color.ToLowerInvariant();
        db.Entry(milestone).Property(m => m.Version).OriginalValue = request.Version;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChangedElsewhere();
        }

        await MilestonesChangedAsync(projectId, roadmapId, ct);
        return milestone.ToDto();
    }

    [HttpDelete("{roadmapId:guid}/milestones/{milestoneId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> DeleteMilestone(
        Guid projectId, Guid roadmapId, Guid milestoneId, CancellationToken ct)
    {
        var deleted = await db.Milestones
            .Where(m => m.Id == milestoneId && m.RoadmapId == roadmapId && m.Roadmap.ProjectId == projectId)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return NotFound();
        }

        await MilestonesChangedAsync(projectId, roadmapId, ct);
        return NoContent();
    }

    /// <summary>A row to join and a place between rows can't both be where something goes.</summary>
    private ObjectResult? TwoTargets(Guid? joinId, Guid? precedingId, Guid? followingId) =>
        joinId is not null && (precedingId is not null || followingId is not null)
            ? Problem(statusCode: StatusCodes.Status400BadRequest, title: "Two rank targets",
                detail: "Name a row to join or neighbors to go between, not both.")
            : null;

    /// <summary>What the attributes can't say: a name of more than spaces, and a day at all.</summary>
    private ObjectResult? MilestoneProblem(string name, DateOnly day) =>
        string.IsNullOrWhiteSpace(name)
            ? Problem(statusCode: StatusCodes.Status400BadRequest, title: "No name given",
                detail: "Give the milestone a name.")
            : day == default
                ? Problem(statusCode: StatusCodes.Status400BadRequest, title: "No day given",
                    detail: "Say which day the milestone is due.")
                : null;

    private Task MilestonesChangedAsync(Guid projectId, Guid roadmapId, CancellationToken ct) =>
        live.PublishAsync(projectId, RoadmapTopics.Roadmap(roadmapId), RoadmapTopics.MilestonesChanged, ct: ct);

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
