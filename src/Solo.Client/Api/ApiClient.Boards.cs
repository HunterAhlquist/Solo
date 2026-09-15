using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Api;

public sealed partial class ApiClient
{
    // -- Boards -------------------------------------------------------------
    public Task<IReadOnlyList<BoardDto>> GetBoardsAsync(Guid projectId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<BoardDto>>($"api/projects/{projectId}/boards", ct);

    /// <summary>The one-shot render payload for the Kanban view.</summary>
    public Task<BoardDetailDto> GetBoardAsync(Guid boardId, CancellationToken ct = default) =>
        GetAsync<BoardDetailDto>($"api/boards/{boardId}", ct);

    /// <summary>Resolves the aliased board route (/projects/DMD/boards/PW26).</summary>
    public Task<BoardDetailDto> GetBoardByAliasAsync(
        string projectAlias, string boardAlias, CancellationToken ct = default) =>
        GetAsync<BoardDetailDto>(
            $"api/projects/by-alias/{Uri.EscapeDataString(projectAlias)}/boards/{Uri.EscapeDataString(boardAlias)}", ct);

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
}
