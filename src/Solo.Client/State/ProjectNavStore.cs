using Solo.Client.Api;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Client.State;

/// <summary>
/// Backs the sidebar's project → board tree.
///
/// No new API surface: the projects come from <c>GET /api/projects</c> and each
/// project's boards from <c>GET /api/projects/{id}/boards</c>, fetched the first
/// time that project is expanded. Loading every project's boards up front would
/// turn one request into N for a tree the user may never open.
/// </summary>
public sealed class ProjectNavStore(ApiClient api)
{
    private readonly Dictionary<Guid, IReadOnlyList<BoardDto>> _boards = [];
    private readonly HashSet<Guid> _loading = [];
    private Task<IReadOnlyList<ProjectSummaryDto>>? _projects;

    public IReadOnlyList<ProjectSummaryDto> Projects { get; private set; } = [];

    /// <summary>The sidebar re-renders on this.</summary>
    public event Action? Changed;

    /// <summary>Loads the project list once. Concurrent callers share the request.</summary>
    public async Task EnsureProjectsAsync()
    {
        if (_projects is not null)
        {
            return;
        }

        try
        {
            _projects = api.GetProjectsAsync();
            Projects = await _projects;
            Changed?.Invoke();
        }
        catch (ApiException)
        {
            // Don't cache a failure: a later navigation should retry.
            _projects = null;
        }
    }

    /// <summary>Open boards for a project, or null while they're still on the wire.</summary>
    public IReadOnlyList<BoardDto>? BoardsOf(Guid projectId) =>
        _boards.TryGetValue(projectId, out var boards) ? boards : null;

    /// <summary>
    /// Called when a project group is expanded. Closed boards are deliberately
    /// omitted — they live in the project home's faded foldout, per "fold, don't
    /// hide"; a sidebar listing every closed board would bury the live ones.
    /// </summary>
    public async Task EnsureBoardsAsync(Guid projectId)
    {
        if (_boards.ContainsKey(projectId) || !_loading.Add(projectId))
        {
            return;
        }

        try
        {
            var boards = await api.GetBoardsAsync(projectId);
            _boards[projectId] = boards.Where(b => b.IsOpen).OrderBy(b => b.Name).ToList();
            Changed?.Invoke();
        }
        catch (ApiException)
        {
            // Leave it unloaded so expanding again retries.
        }
        finally
        {
            _loading.Remove(projectId);
        }
    }

    /// <summary>A project was created, renamed or deleted somewhere in the app.</summary>
    public async Task RefreshProjectsAsync()
    {
        _projects = null;
        await EnsureProjectsAsync();
    }

    /// <summary>A board was created, renamed, closed or reopened in this project.</summary>
    public async Task RefreshBoardsAsync(Guid projectId)
    {
        _boards.Remove(projectId);
        await EnsureBoardsAsync(projectId);
    }

    /// <summary>Whether the signed-in user may add a board to this project.</summary>
    public static bool CanAddBoard(ProjectSummaryDto project, bool isSuperAdmin) =>
        isSuperAdmin || project.IsOwner || project.Role == ProjectRole.ReadWrite;
}
