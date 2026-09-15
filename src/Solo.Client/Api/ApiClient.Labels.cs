using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Api;

public sealed partial class ApiClient
{
    // -- Labels -------------------------------------------------------------
    /// <summary>The project's label defaults — the template new boards copy.</summary>
    public Task<IReadOnlyList<LabelDto>> GetLabelsAsync(
        Guid projectId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LabelDto>>($"api/projects/{projectId}/labels", ct);

    public Task<LabelDto> CreateLabelAsync(
        Guid projectId, CreateLabelRequest request, CancellationToken ct = default) =>
        SendAsync<LabelDto>(HttpMethod.Post, $"api/projects/{projectId}/labels", request, ct);

    public Task<LabelDto> UpdateLabelAsync(
        Guid projectId, Guid labelId, UpdateLabelRequest request, CancellationToken ct = default) =>
        SendAsync<LabelDto>(HttpMethod.Put, $"api/projects/{projectId}/labels/{labelId}", request, ct);

    public Task DeleteLabelAsync(Guid projectId, Guid labelId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/projects/{projectId}/labels/{labelId}", null, ct);

    /// <summary>The labels this board owns — the only ones its items can carry.</summary>
    public Task<IReadOnlyList<LabelDto>> GetBoardLabelsAsync(Guid boardId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LabelDto>>($"api/boards/{boardId}/labels", ct);

    public Task<LabelDto> CreateBoardLabelAsync(
        Guid boardId, CreateLabelRequest request, CancellationToken ct = default) =>
        SendAsync<LabelDto>(HttpMethod.Post, $"api/boards/{boardId}/labels", request, ct);

    public Task<LabelDto> UpdateBoardLabelAsync(
        Guid boardId, Guid labelId, UpdateLabelRequest request, CancellationToken ct = default) =>
        SendAsync<LabelDto>(HttpMethod.Put, $"api/boards/{boardId}/labels/{labelId}", request, ct);

    public Task DeleteBoardLabelAsync(Guid boardId, Guid labelId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/boards/{boardId}/labels/{labelId}", null, ct);

    /// <summary>Puts the board's labels back to the project defaults.</summary>
    public Task<IReadOnlyList<LabelDto>> ResetBoardLabelsAsync(Guid boardId, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<LabelDto>>(HttpMethod.Post, $"api/boards/{boardId}/labels/reset", null, ct);
}
