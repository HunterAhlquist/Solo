using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Extensibility;
using Solo.Roadmap.Contracts;
using Solo.Roadmap.Data;

namespace Solo.Roadmap.Controllers;

/// <summary>The roadmaps a task is on, for the chips in its task view.</summary>
[ApiController]
[Route("tasks")]
public sealed class RoadmapTasksController(RoadmapDbContext db, ISoloQuery query) : ControllerBase
{
    [HttpGet("{taskId:guid}/roadmaps")]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<ActionResult<IReadOnlyList<TaskRoadmapDto>>> ForTask(Guid taskId, CancellationToken ct)
    {
        if (await query.TaskAsync(taskId, ct) is null)
        {
            return NotFound();
        }

        var roadmaps = await db.Entries.AsNoTracking()
            .Where(e => e.TaskId == taskId)
            .Select(e => e.Roadmap)
            .OrderBy(r => r.Title)
            .Select(r => new { r.Id, r.Title, r.ProjectId })
            .ToListAsync(ct);

        // A task never leaves its project, so these are all the caller's own
        // project's roadmaps; the membership filter is belt and braces.
        var aliases = (await query.ProjectsAsync(ct)).ToDictionary(p => p.Id, p => p.Alias);
        return roadmaps
            .Where(r => aliases.ContainsKey(r.ProjectId))
            .Select(r => new TaskRoadmapDto(r.Id, r.Title, aliases[r.ProjectId]))
            .ToList();
    }
}
