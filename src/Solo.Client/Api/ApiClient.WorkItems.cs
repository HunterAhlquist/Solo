using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Api;

public sealed partial class ApiClient
{
    // -- Epics --------------------------------------------------------------
    public Task<IReadOnlyList<EpicDto>> GetEpicsAsync(Guid boardId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<EpicDto>>($"api/boards/{boardId}/epics", ct);

    public Task<EpicDto> CreateEpicAsync(
        Guid boardId, CreateEpicRequest request, CancellationToken ct = default) =>
        SendAsync<EpicDto>(HttpMethod.Post, $"api/boards/{boardId}/epics", request, ct);

    public Task<EpicDto> UpdateEpicAsync(
        Guid epicId, UpdateEpicRequest request, CancellationToken ct = default) =>
        SendAsync<EpicDto>(HttpMethod.Put, $"api/epics/{epicId}", request, ct);

    public Task DeleteEpicAsync(Guid epicId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/epics/{epicId}", null, ct);

    /// <summary>Place the epic between two siblings; the server computes the fractional rank.</summary>
    public Task<EpicDto> RerankEpicAsync(
        Guid epicId, Guid? precedingId, Guid? followingId, CancellationToken ct = default) =>
        SendAsync<EpicDto>(HttpMethod.Put, $"api/epics/{epicId}/rank",
            new RerankRequest { PrecedingId = precedingId, FollowingId = followingId }, ct);

    public Task<EpicDto> SetEpicAssigneesAsync(
        Guid epicId, IReadOnlyList<Guid> userIds, CancellationToken ct = default) =>
        SendAsync<EpicDto>(HttpMethod.Put, $"api/epics/{epicId}/assignees",
            new SetAssigneesRequest { UserIds = userIds }, ct);

    // -- Tasks --------------------------------------------------------------
    public Task<IReadOnlyList<TaskDto>> GetTasksAsync(Guid boardId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<TaskDto>>($"api/boards/{boardId}/tasks", ct);

    public Task<TaskDto> GetTaskAsync(Guid taskId, CancellationToken ct = default) =>
        GetAsync<TaskDto>($"api/tasks/{taskId}", ct);

    public Task<TaskDto> CreateTaskAsync(
        Guid boardId, CreateTaskRequest request, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Post, $"api/boards/{boardId}/tasks", request, ct);

    public Task<TaskDto> UpdateTaskAsync(
        Guid taskId, UpdateTaskRequest request, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Put, $"api/tasks/{taskId}", request, ct);

    public Task DeleteTaskAsync(Guid taskId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/tasks/{taskId}", null, ct);

    /// <summary>Column move. The server rejects this for tasks whose status is derived from sub-tasks.</summary>
    public Task<TaskDto> SetTaskStatusAsync(
        Guid taskId, TaskState status, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Put, $"api/tasks/{taskId}/status",
            new SetTaskStatusRequest { Status = status }, ct);

    /// <summary>Swimlane move; <c>null</c> drops the task into the ad-hoc band.</summary>
    public Task<TaskDto> SetTaskEpicAsync(
        Guid taskId, Guid? epicId, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Put, $"api/tasks/{taskId}/epic",
            new SetTaskEpicRequest { EpicId = epicId }, ct);

    /// <summary>
    /// Board transfer within the project. The task lands in the destination's
    /// ad-hoc band unless <paramref name="epicId"/> names one of its epics, and
    /// comes back renumbered — its serial belongs to the new board now.
    /// </summary>
    public Task<TaskDto> MoveTaskAsync(
        Guid taskId, Guid boardId, Guid? epicId, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Put, $"api/tasks/{taskId}/board",
            new MoveTaskRequest { BoardId = boardId, EpicId = epicId }, ct);

    public Task<TaskDto> RerankTaskAsync(
        Guid taskId, Guid? precedingId, Guid? followingId, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Put, $"api/tasks/{taskId}/rank",
            new RerankRequest { PrecedingId = precedingId, FollowingId = followingId }, ct);

    public Task<TaskDto> SetTaskAssigneesAsync(
        Guid taskId, IReadOnlyList<Guid> userIds, CancellationToken ct = default) =>
        SendAsync<TaskDto>(HttpMethod.Put, $"api/tasks/{taskId}/assignees",
            new SetAssigneesRequest { UserIds = userIds }, ct);

    // -- Sub-tasks ----------------------------------------------------------
    public Task<IReadOnlyList<SubTaskDto>> GetSubTasksAsync(Guid taskId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<SubTaskDto>>($"api/tasks/{taskId}/subtasks", ct);

    public Task<SubTaskDto> CreateSubTaskAsync(
        Guid taskId, CreateSubTaskRequest request, CancellationToken ct = default) =>
        SendAsync<SubTaskDto>(HttpMethod.Post, $"api/tasks/{taskId}/subtasks", request, ct);

    public Task<SubTaskDto> UpdateSubTaskAsync(
        Guid subTaskId, UpdateSubTaskRequest request, CancellationToken ct = default) =>
        SendAsync<SubTaskDto>(HttpMethod.Put, $"api/subtasks/{subTaskId}", request, ct);

    public Task DeleteSubTaskAsync(Guid subTaskId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/subtasks/{subTaskId}", null, ct);

    /// <summary>Checklist toggle; the server recomputes the parent task's status.</summary>
    public Task<SubTaskDto> SetSubTaskFinishedAsync(
        Guid subTaskId, bool isFinished, CancellationToken ct = default) =>
        SendAsync<SubTaskDto>(HttpMethod.Put, $"api/subtasks/{subTaskId}/finished",
            new SetSubTaskFinishedRequest { IsFinished = isFinished }, ct);

    public Task<SubTaskDto> RerankSubTaskAsync(
        Guid subTaskId, Guid? precedingId, Guid? followingId, CancellationToken ct = default) =>
        SendAsync<SubTaskDto>(HttpMethod.Put, $"api/subtasks/{subTaskId}/rank",
            new RerankRequest { PrecedingId = precedingId, FollowingId = followingId }, ct);
}
