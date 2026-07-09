using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Client.Api;

/// <summary>
/// One method per API endpoint, returning <c>Solo.Shared</c> DTOs directly — no
/// client-side model duplication, no codegen. Mutations send the DTO's
/// <c>Version</c> (Postgres xmin) for optimistic concurrency; a 409 surfaces as
/// <see cref="ApiConflictException"/>.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    // -- Bootstrap ----------------------------------------------------------
    public Task<AuthConfigDto> GetAuthConfigAsync(CancellationToken ct = default) =>
        GetAsync<AuthConfigDto>("api/config/auth", ct);

    public Task<SetupStatusDto> GetSetupStatusAsync(CancellationToken ct = default) =>
        GetAsync<SetupStatusDto>("api/setup/status", ct);

    public Task<UserDto> GetCurrentUserAsync(CancellationToken ct = default) =>
        GetAsync<UserDto>("api/users/me", ct);

    /// <summary>
    /// Final setup step. Called by the SSO-authenticated operator; the static
    /// credentials in the body re-prove they are the one who ran the wizard.
    /// </summary>
    public Task<UserDto> CompleteSetupAsync(CompleteSetupRequest request, CancellationToken ct = default) =>
        SendAsync<UserDto>(HttpMethod.Post, "api/setup/complete", request, ct);

    // -- Instance settings --------------------------------------------------
    public Task<InstanceSettingsDto> GetInstanceSettingsAsync(CancellationToken ct = default) =>
        GetAsync<InstanceSettingsDto>("api/settings", ct);

    public Task<InstanceSettingsDto> UpdateInstanceSettingsAsync(
        UpdateInstanceSettingsRequest request, CancellationToken ct = default) =>
        SendAsync<InstanceSettingsDto>(HttpMethod.Put, "api/settings", request, ct);

    // -- Projects -----------------------------------------------------------
    public Task<IReadOnlyList<ProjectSummaryDto>> GetProjectsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ProjectSummaryDto>>("api/projects", ct);

    public Task<ProjectDto> GetProjectAsync(Guid projectId, CancellationToken ct = default) =>
        GetAsync<ProjectDto>($"api/projects/{projectId}", ct);

    public Task<ProjectDto> CreateProjectAsync(CreateProjectRequest request, CancellationToken ct = default) =>
        SendAsync<ProjectDto>(HttpMethod.Post, "api/projects", request, ct);

    public Task<ProjectDto> UpdateProjectAsync(
        Guid projectId, UpdateProjectRequest request, CancellationToken ct = default) =>
        SendAsync<ProjectDto>(HttpMethod.Put, $"api/projects/{projectId}", request, ct);

    public Task DeleteProjectAsync(Guid projectId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/projects/{projectId}", null, ct);

    // -- Project members ----------------------------------------------------
    public Task<IReadOnlyList<ProjectMemberDto>> GetMembersAsync(Guid projectId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ProjectMemberDto>>($"api/projects/{projectId}/members", ct);

    public Task<ProjectMemberDto> InviteMemberAsync(
        Guid projectId, InviteMemberRequest request, CancellationToken ct = default) =>
        SendAsync<ProjectMemberDto>(HttpMethod.Post, $"api/projects/{projectId}/members", request, ct);

    public Task<ProjectMemberDto> UpdateMemberAsync(
        Guid projectId, Guid memberId, UpdateMemberRequest request, CancellationToken ct = default) =>
        SendAsync<ProjectMemberDto>(HttpMethod.Put, $"api/projects/{projectId}/members/{memberId}", request, ct);

    public Task RemoveMemberAsync(Guid projectId, Guid memberId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/projects/{projectId}/members/{memberId}", null, ct);

    // -- Boards -------------------------------------------------------------
    public Task<IReadOnlyList<BoardDto>> GetBoardsAsync(Guid projectId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<BoardDto>>($"api/projects/{projectId}/boards", ct);

    /// <summary>The one-shot render payload for the Kanban view.</summary>
    public Task<BoardDetailDto> GetBoardAsync(Guid boardId, CancellationToken ct = default) =>
        GetAsync<BoardDetailDto>($"api/boards/{boardId}", ct);

    public Task<BoardDto> CreateBoardAsync(
        Guid projectId, CreateBoardRequest request, CancellationToken ct = default) =>
        SendAsync<BoardDto>(HttpMethod.Post, $"api/projects/{projectId}/boards", request, ct);

    public Task<BoardDto> UpdateBoardAsync(
        Guid boardId, UpdateBoardRequest request, CancellationToken ct = default) =>
        SendAsync<BoardDto>(HttpMethod.Put, $"api/boards/{boardId}", request, ct);

    public Task<BoardDto> SetBoardOpenAsync(
        Guid boardId, bool isOpen, CancellationToken ct = default) =>
        SendAsync<BoardDto>(HttpMethod.Put, $"api/boards/{boardId}/open",
            new SetBoardOpenRequest { IsOpen = isOpen }, ct);

    public Task DeleteBoardAsync(Guid boardId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/boards/{boardId}", null, ct);

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

    // -- Comments -----------------------------------------------------------
    public Task<IReadOnlyList<CommentDto>> GetEpicCommentsAsync(Guid epicId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<CommentDto>>($"api/epics/{epicId}/comments", ct);

    public Task<IReadOnlyList<CommentDto>> GetTaskCommentsAsync(Guid taskId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<CommentDto>>($"api/tasks/{taskId}/comments", ct);

    public Task<CommentDto> AddEpicCommentAsync(
        Guid epicId, string body, CancellationToken ct = default) =>
        SendAsync<CommentDto>(HttpMethod.Post, $"api/epics/{epicId}/comments",
            new CreateCommentRequest { Body = body }, ct);

    public Task<CommentDto> AddTaskCommentAsync(
        Guid taskId, string body, CancellationToken ct = default) =>
        SendAsync<CommentDto>(HttpMethod.Post, $"api/tasks/{taskId}/comments",
            new CreateCommentRequest { Body = body }, ct);

    public Task<CommentDto> UpdateCommentAsync(
        Guid commentId, UpdateCommentRequest request, CancellationToken ct = default) =>
        SendAsync<CommentDto>(HttpMethod.Put, $"api/comments/{commentId}", request, ct);

    public Task DeleteCommentAsync(Guid commentId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/comments/{commentId}", null, ct);

    // -- Labels -------------------------------------------------------------
    /// <summary>Project-global labels, plus the board-local additions when <paramref name="boardId"/> is given.</summary>
    public Task<IReadOnlyList<LabelDto>> GetLabelsAsync(
        Guid projectId, Guid? boardId = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LabelDto>>(
            boardId is null
                ? $"api/projects/{projectId}/labels"
                : $"api/projects/{projectId}/labels?boardId={boardId}", ct);

    public Task<LabelDto> CreateLabelAsync(
        Guid projectId, CreateLabelRequest request, CancellationToken ct = default) =>
        SendAsync<LabelDto>(HttpMethod.Post, $"api/projects/{projectId}/labels", request, ct);

    public Task<LabelDto> UpdateLabelAsync(
        Guid projectId, Guid labelId, UpdateLabelRequest request, CancellationToken ct = default) =>
        SendAsync<LabelDto>(HttpMethod.Put, $"api/projects/{projectId}/labels/{labelId}", request, ct);

    public Task DeleteLabelAsync(Guid projectId, Guid labelId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/projects/{projectId}/labels/{labelId}", null, ct);

    // -- Images -------------------------------------------------------------

    /// <summary>Relative path the browser can put straight into an <c>&lt;img src&gt;</c>.</summary>
    public static string ImageUrl(Guid imageId) => $"api/images/{imageId}";

    /// <summary>
    /// Same-origin path for a user's SSO profile picture, proxied and cached by the
    /// API. Loading the provider CDN directly gets the browser rate-limited (429).
    /// </summary>
    public static string AvatarUrl(Guid userId) => $"api/users/{userId}/avatar";

    /// <summary>Uploads a custom icon image; the returned id becomes an entity's <c>ImageId</c>.</summary>
    public async Task<ImageAssetDto> UploadImageAsync(
        Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        using var response = await http.PostAsync("api/images", form, ct);
        return await ReadAsync<ImageAssetDto>(response, ct);
    }

    // -- Audit --------------------------------------------------------------
    public Task<PagedResult<AuditEntryDto>> GetAuditAsync(
        Guid projectId,
        string? table = null,
        Guid? userId = null,
        DateTime? from = null,
        DateTime? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(table))
        {
            query.Add($"table={Uri.EscapeDataString(table)}");
        }

        if (userId is not null)
        {
            query.Add($"userId={userId}");
        }

        if (from is not null)
        {
            query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        }

        if (to is not null)
        {
            query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        }

        return GetAsync<PagedResult<AuditEntryDto>>(
            $"api/projects/{projectId}/audit?{string.Join('&', query)}", ct);
    }

    // -- Plumbing -----------------------------------------------------------
    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = BuildRequest(method, url, body);
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = BuildRequest(method, url, body);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: SoloJson.Options);
        }

        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        var value = await response.Content.ReadFromJsonAsync<T>(SoloJson.Options, ct);
        return value ?? throw new ApiException(
            response.StatusCode, "Empty response", $"The API returned no body for a {typeof(T).Name}.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var (title, detail) = await ReadProblemAsync(response, ct);
        throw response.StatusCode == HttpStatusCode.Conflict
            ? new ApiConflictException(title, detail)
            : new ApiException(response.StatusCode, title, detail);
    }

    private static async Task<(string? Title, string? Detail)> ReadProblemAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            return (
                root.TryGetProperty("title", out var title) ? title.GetString() : null,
                root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            // Not a ProblemDetails body (a proxy error page, say). The status
            // code alone is what the caller gets.
            return (null, null);
        }
    }
}
