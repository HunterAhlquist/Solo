using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Api;

public sealed partial class ApiClient
{
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
}
