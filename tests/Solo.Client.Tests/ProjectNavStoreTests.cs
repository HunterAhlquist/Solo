using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Extensions;
using Solo.Client.Layout;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>The store is the sidebar's source of truth; these pin its fetch rules.</summary>
public class ProjectNavStoreTests
{
    [Fact]
    public async Task Projects_are_fetched_once_however_many_callers_ask()
    {
        var http = new StubHttp();
        http.RouteJson("/api/projects", new[] { new ProjectSummaryDto(TestData.ProjectId, "Solo", "SOLO", null, null, ProjectRole.Read, false) });
        var store = new ProjectNavStore(http.CreateClient());

        await store.EnsureProjectsAsync();
        await store.EnsureProjectsAsync();

        Assert.Single(http.Paths, p => p.Contains("/api/projects"));
    }

    [Fact]
    public async Task Boards_are_cached_per_project()
    {
        var http = new StubHttp();
        http.RouteJson("/boards", new[] { new BoardDto(TestData.BoardId, TestData.ProjectId, "Main", "MAIN", null, null, true, 1) });
        var store = new ProjectNavStore(http.CreateClient());

        await store.EnsureBoardsAsync(TestData.ProjectId);
        await store.EnsureBoardsAsync(TestData.ProjectId);

        Assert.Single(http.Paths, p => p.Contains("/boards"));
        Assert.Equal("Main", store.BoardsOf(TestData.ProjectId)!.Single().Name);
    }

    [Fact]
    public async Task Refreshing_boards_refetches_them()
    {
        var http = new StubHttp();
        http.RouteJson("/boards", new[] { new BoardDto(TestData.BoardId, TestData.ProjectId, "Main", "MAIN", null, null, true, 1) });
        var store = new ProjectNavStore(http.CreateClient());

        await store.EnsureBoardsAsync(TestData.ProjectId);
        await store.RefreshBoardsAsync(TestData.ProjectId);

        Assert.Equal(2, http.Paths.Count(p => p.Contains("/boards")));
    }

    [Fact]
    public async Task A_failed_project_load_is_not_cached_so_the_next_navigation_retries()
    {
        var http = new StubHttp();
        http.Respond(System.Net.HttpStatusCode.InternalServerError);
        http.RespondJson(new[] { new ProjectSummaryDto(TestData.ProjectId, "Solo", "SOLO", null, null, ProjectRole.Read, false) });
        var store = new ProjectNavStore(http.CreateClient());

        await store.EnsureProjectsAsync();
        Assert.Empty(store.Projects);

        await store.EnsureProjectsAsync();
        Assert.Single(store.Projects);
    }

    // The super admin's blanket rights stop at the owner surface, so being one
    // plays no part here: only the caller's own membership grants write access.
    [Theory]
    [InlineData(ProjectRole.Read, false, false)]
    [InlineData(ProjectRole.ReadWrite, false, true)]
    [InlineData(ProjectRole.Read, true, true)]   // owner
    public void Write_permission_follows_the_APIs_write_rule(
        ProjectRole role, bool isOwner, bool expected)
    {
        var project = new ProjectSummaryDto(TestData.ProjectId, "Solo", "SOLO", null, null, role, isOwner);

        Assert.Equal(expected, ProjectNavStore.CanWrite(project));
    }
}
