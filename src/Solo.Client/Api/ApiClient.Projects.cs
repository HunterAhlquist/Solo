using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Api;

public sealed partial class ApiClient
{
    // -- Projects -----------------------------------------------------------
    public Task<IReadOnlyList<ProjectSummaryDto>> GetProjectsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ProjectSummaryDto>>("api/projects", ct);

    public Task<ProjectDto> GetProjectAsync(Guid projectId, CancellationToken ct = default) =>
        GetAsync<ProjectDto>($"api/projects/{projectId}", ct);

    /// <summary>Resolves the aliased project route (/projects/DMD).</summary>
    public Task<ProjectDto> GetProjectByAliasAsync(string projectAlias, CancellationToken ct = default) =>
        GetAsync<ProjectDto>($"api/projects/by-alias/{Uri.EscapeDataString(projectAlias)}", ct);

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
}
