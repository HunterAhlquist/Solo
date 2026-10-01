using Solo.Roadmap.Contracts;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Roadmap.Client;

/// <summary>
/// The calls the Roadmaps pages make: the extension's own endpoints, and the
/// handful of core ones it reads or — for a task's dates — writes like any
/// other client.
/// </summary>
/// <remarks>
/// A thin layer over <see cref="ISoloClientApi"/>, the host's authenticated
/// HTTP path, so the extension can't end up authenticating differently from the
/// app it runs inside. Constructed per component rather than registered: a
/// client half can't add services, because its assembly loads after the
/// container is built.
/// </remarks>
public sealed class RoadmapApi(ISoloClientApi api)
{
    private const string Base = "api/ext/roadmap";

    private static readonly string PackageVersion =
        typeof(RoadmapApi).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>Absolute URL of a file in the package's <c>content/</c> folder, fetched by this browser.</summary>
    /// <remarks>
    /// Carries the package version, so the page and its script always match. The
    /// server sends these files without cache headers, but a proxy in front of
    /// it may add its own, and the desktop app's cache outlives a restart. With
    /// an unversioned URL, 1.2.1's page ran beside a cached 1.2.0 roadmap.js:
    /// no add marker, and clicks on empty days went nowhere.
    /// </remarks>
    public Uri Content(string fileName) =>
        api.Resolve($"api/extensions/roadmap/content/{fileName}?v={PackageVersion}");

    /// <summary>Absolute URL of a user's avatar, through core's same-origin proxy.</summary>
    public Uri Avatar(Guid userId) => api.Resolve($"api/users/{userId}/avatar");

    // -- The extension's endpoints -------------------------------------------

    public Task<List<RoadmapSummaryDto>> ListAsync(Guid projectId, CancellationToken ct = default) =>
        api.GetAsync<List<RoadmapSummaryDto>>($"{Base}/projects/{projectId}/roadmaps", ct);

    public Task<RoadmapSummaryDto> CreateAsync(Guid projectId, string title, CancellationToken ct = default) =>
        api.SendAsync<RoadmapSummaryDto>(HttpMethod.Post, $"{Base}/projects/{projectId}/roadmaps",
            new CreateRoadmapRequest { Title = title }, ct);

    public Task<RoadmapSummaryDto> RenameAsync(
        Guid projectId, Guid roadmapId, string title, uint version, CancellationToken ct = default) =>
        api.SendAsync<RoadmapSummaryDto>(HttpMethod.Put, $"{Base}/projects/{projectId}/roadmaps/{roadmapId}",
            new RenameRoadmapRequest { Title = title, Version = version }, ct);

    public Task DeleteAsync(Guid projectId, Guid roadmapId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{Base}/projects/{projectId}/roadmaps/{roadmapId}", null, ct);

    public Task<RoadmapTimelineDto> TimelineAsync(Guid projectId, Guid roadmapId, CancellationToken ct = default) =>
        api.GetAsync<RoadmapTimelineDto>($"{Base}/projects/{projectId}/roadmaps/{roadmapId}", ct);

    public Task<List<RoadmapCandidateDto>> CandidatesAsync(
        Guid projectId, Guid roadmapId, CancellationToken ct = default) =>
        api.GetAsync<List<RoadmapCandidateDto>>($"{Base}/projects/{projectId}/roadmaps/{roadmapId}/candidates", ct);

    public Task<RoadmapEntryDto> AddEntryAsync(
        Guid projectId, Guid roadmapId, Guid taskId, DateOnly anchorDay, CancellationToken ct = default) =>
        api.SendAsync<RoadmapEntryDto>(HttpMethod.Post, $"{Base}/projects/{projectId}/roadmaps/{roadmapId}/entries",
            new AddRoadmapEntryRequest { TaskId = taskId, AnchorDay = anchorDay }, ct);

    public Task RerankAsync(Guid projectId, Guid roadmapId, Guid entryId, Guid? precedingId, Guid? followingId,
        CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/projects/{projectId}/roadmaps/{roadmapId}/entries/{entryId}/rank",
            new RerankRoadmapEntryRequest { PrecedingId = precedingId, FollowingId = followingId }, ct);

    public Task SetAnchorAsync(Guid projectId, Guid roadmapId, Guid entryId, DateOnly anchorDay,
        CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Put, $"{Base}/projects/{projectId}/roadmaps/{roadmapId}/entries/{entryId}/anchor",
            new SetRoadmapEntryAnchorRequest { AnchorDay = anchorDay }, ct);

    public Task RemoveEntryAsync(Guid projectId, Guid roadmapId, Guid entryId, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete,
            $"{Base}/projects/{projectId}/roadmaps/{roadmapId}/entries/{entryId}", null, ct);

    public Task<List<TaskRoadmapDto>> RoadmapsOfTaskAsync(Guid taskId, CancellationToken ct = default) =>
        api.GetAsync<List<TaskRoadmapDto>>($"{Base}/tasks/{taskId}/roadmaps", ct);

    public Task<RoadmapSettingsDto> SettingsAsync(CancellationToken ct = default) =>
        api.GetAsync<RoadmapSettingsDto>($"{Base}/settings", ct);

    public Task<RoadmapSettingsDto> SaveSettingsAsync(
        UpdateRoadmapSettingsRequest request, CancellationToken ct = default) =>
        api.SendAsync<RoadmapSettingsDto>(HttpMethod.Put, $"{Base}/settings", request, ct);

    // -- Core ------------------------------------------------------------------

    public Task<List<ProjectSummaryDto>> ProjectsAsync(CancellationToken ct = default) =>
        api.GetAsync<List<ProjectSummaryDto>>("api/projects", ct);

    public Task<List<ProjectMemberDto>> MembersAsync(Guid projectId, CancellationToken ct = default) =>
        api.GetAsync<List<ProjectMemberDto>>($"api/projects/{projectId}/members", ct);

    public Task<List<LabelDto>> LabelsAsync(Guid projectId, CancellationToken ct = default) =>
        api.GetAsync<List<LabelDto>>($"api/projects/{projectId}/labels", ct);

    public Task<TaskDto> TaskAsync(Guid taskId, CancellationToken ct = default) =>
        api.GetAsync<TaskDto>($"api/tasks/{taskId}", ct);

    /// <summary>
    /// A task's dates, through core's own endpoint — the extension's server
    /// never writes a core row. Core checks the pair and the version, runs any
    /// hooks, audits it and tells open boards.
    /// </summary>
    public Task<TaskDto> SetScheduleAsync(
        Guid taskId, DateOnly? startAt, DateOnly? deadlineAt, uint version, CancellationToken ct = default) =>
        api.SendAsync<TaskDto>(HttpMethod.Put, $"api/tasks/{taskId}/schedule",
            new SetTaskScheduleRequest { StartAt = startAt, DeadlineAt = deadlineAt, Version = version }, ct);
}
