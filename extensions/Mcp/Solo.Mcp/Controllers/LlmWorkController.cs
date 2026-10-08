using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;
using Solo.Mcp.Data;
using Solo.Shared.Dtos;

namespace Solo.Mcp.Controllers;

[ApiController]
[Route("tasks")]
public sealed class LlmWorkController(
    ISoloQuery query,
    McpDbContext db,
    [FromKeyedServices("mcp")] IExtensionFieldStore fields) : ControllerBase
{
    [HttpGet("{taskId:guid}/llm-work")]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<ActionResult<LlmWorkDto>> Get(Guid taskId, CancellationToken ct)
    {
        var task = await query.TaskAsync(taskId, ct);
        return task is null ? NotFound() : await ReadAsync(task, ct);
    }

    [HttpPut("{taskId:guid}/llm-work")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<LlmWorkDto>> Set(Guid taskId, SetLlmWorkRequest request, CancellationToken ct)
    {
        var task = await query.TaskAsync(taskId, ct);
        if (task is null) return NotFound();
        var eligible = await EligibleAsync(task.Id, ct);
        if (request.LlmWork && eligible.Count == 0)
            return Problem(statusCode: 400, title: "No assignee has MCP access",
                detail: "Assign a user with active MCP access before marking this task as LLM work.");

        try
        {
            await fields.SetVersionedAsync(ExtensionEntity.Task, taskId,
                new LlmWorkFields(request.LlmWork, request.LlmWork ? eligible : []), request.Version, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problem(statusCode: 409, title: "Task changed",
                detail: "Refresh the task and try again.");
        }

        return await ReadAsync((await query.TaskAsync(taskId, ct))!, ct);
    }

    [HttpGet("boards/{boardId:guid}/llm-work")]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<ActionResult<IReadOnlyList<LlmTaskDto>>> List(Guid boardId, CancellationToken ct)
    {
        var result = new List<LlmTaskDto>();
        foreach (var task in await query.TasksAsync(boardId, ct))
        {
            var work = await ReadAsync(task, ct);
            if (work.LlmWork) result.Add(new LlmTaskDto(task, work));
        }
        return result;
    }

    private async Task<LlmWorkDto> ReadAsync(TaskDto task, CancellationToken ct)
    {
        var eligible = await EligibleAsync(task.Id, ct);
        var saved = await fields.GetAsync<LlmWorkFields>(ExtensionEntity.Task, task.Id, ct);
        // A flag belongs to the assignees who were offered the work. Reassigning
        // the task must not silently offer it to another person's agent.
        var assigned = saved?.AssigneeIds.Intersect(eligible).ToList() ?? [];
        return new LlmWorkDto(task.Id, eligible.Count > 0,
            saved?.LlmWork == true && assigned.Count > 0, assigned, task.Version);
    }

    private async Task<List<Guid>> EligibleAsync(Guid taskId, CancellationToken ct)
    {
        var eligible = new List<Guid>();
        foreach (var user in await query.TaskAssigneeIdentitiesAsync(taskId, ct))
        {
            if (await db.Tokens.AnyAsync(t => t.Issuer == user.Issuer && t.Subject == user.Subject &&
                    t.RevokedAt == null, ct))
                eligible.Add(user.UserId);
        }
        return eligible;
    }
}

public sealed record LlmWorkFields(bool LlmWork, IReadOnlyList<Guid> AssigneeIds);
public sealed record LlmWorkDto(Guid TaskId, bool Eligible, bool LlmWork,
    IReadOnlyList<Guid> AssigneeIds, uint Version);
public sealed record LlmTaskDto(TaskDto Task, LlmWorkDto Work);
public sealed record SetLlmWorkRequest(bool LlmWork, uint Version);
