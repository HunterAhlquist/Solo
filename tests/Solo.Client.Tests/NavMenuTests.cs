using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Layout;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The sidebar is the app's map: projects, and each project's open boards on
/// demand. Boards must not be fetched until a project is expanded — otherwise
/// signing in costs one request per project for a tree nobody opened.
/// </summary>
public class NavMenuTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid OtherProjectId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private readonly StubHttp _http = new();

    public NavMenuTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);

        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new UserSession(api));
        Services.AddSingleton(new ProjectNavStore(api));
        Services.AddScoped<RecentProjectsStore>();
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private static ProjectSummaryDto Project(Guid id, string name, ProjectRole role, bool isOwner) =>
        new(id, name, "Games", null, role, isOwner);

    private static BoardDto Board(Guid id, string name, bool isOpen = true) =>
        new(id, TestData.ProjectId, name, "Rocket", null, isOpen, 1);

    private void Route(bool isSuperAdmin, ProjectRole role = ProjectRole.ReadWrite, bool isOwner = false,
        IEnumerable<BoardDto>? boards = null)
    {
        _http.RouteJson("/api/users/me", new UserDto(TestData.MeId, "h@x.io", "Hunter", null, isSuperAdmin));
        // Board routes are registered first: RouteJson matches on substring, and
        // "/api/projects" would otherwise swallow "/api/projects/{id}/boards".
        _http.RouteJson($"/api/projects/{TestData.ProjectId}/boards",
            boards ?? [Board(TestData.BoardId, "Main"), Board(Guid.NewGuid(), "Archive", isOpen: false)]);
        _http.RouteJson($"/api/projects/{OtherProjectId}/boards", Array.Empty<BoardDto>());
        _http.RouteJson("/api/projects", new[]
        {
            Project(TestData.ProjectId, "Solo", role, isOwner),
            Project(OtherProjectId, "Side quest", ProjectRole.Read, false),
        });
    }

    private IRenderedComponent<NavMenu> RenderNav() => Render<NavMenu>();

    private static void Expand(IRenderedComponent<NavMenu> nav, string projectName) =>
        nav.FindAll(".mud-nav-link").First(l => l.TextContent.Contains(projectName)).Click();

    private static List<string> Links(IRenderedComponent<NavMenu> nav) =>
        nav.FindAll(".mud-nav-link").Select(l => l.TextContent.Trim()).ToList();

    // -- The tree ------------------------------------------------------------

    [Fact]
    public void Every_project_the_user_can_see_is_listed()
    {
        Route(isSuperAdmin: false);

        var links = Links(RenderNav());

        Assert.Contains(links, l => l.Contains("Solo"));
        Assert.Contains(links, l => l.Contains("Side quest"));
    }

    [Fact]
    public void Boards_are_not_fetched_until_a_project_is_expanded()
    {
        Route(isSuperAdmin: false);

        RenderNav();

        Assert.DoesNotContain(_http.Paths, p => p.Contains("/boards"));
    }

    [Fact]
    public void Expanding_a_project_lists_its_open_boards()
    {
        Route(isSuperAdmin: false);
        var nav = RenderNav();

        Expand(nav, "Solo");

        Assert.Contains($"GET /api/projects/{TestData.ProjectId}/boards", _http.Paths);
        Assert.Contains(Links(nav), l => l.Contains("Main"));
    }

    [Fact]
    public void Closed_boards_stay_out_of_the_sidebar()
    {
        // They're reachable from the project home's faded foldout; listing them
        // here would bury the boards actually in play.
        Route(isSuperAdmin: false);
        var nav = RenderNav();

        Expand(nav, "Solo");

        Assert.DoesNotContain(Links(nav), l => l.Contains("Archive"));
    }

    [Fact]
    public void A_board_link_points_at_the_board_route()
    {
        Route(isSuperAdmin: false);
        var nav = RenderNav();

        Expand(nav, "Solo");

        var href = nav.FindAll("a.mud-nav-link")
            .First(a => a.TextContent.Contains("Main"))
            .GetAttribute("href");

        Assert.Equal($"projects/{TestData.ProjectId}/boards/{TestData.BoardId}", href);
    }

    // -- Create affordances --------------------------------------------------

    [Fact]
    public void Add_project_is_offered_to_a_super_admin()
    {
        Route(isSuperAdmin: true);

        Assert.Contains(Links(RenderNav()), l => l.Contains("Add project"));
    }

    [Fact]
    public void Add_project_is_hidden_from_everyone_else()
    {
        // The API restricts project creation to the super admin; don't offer a
        // button that will 403.
        Route(isSuperAdmin: false, isOwner: true);

        Assert.DoesNotContain(Links(RenderNav()), l => l.Contains("Add project"));
    }

    [Fact]
    public void Add_board_is_offered_inside_a_writable_project()
    {
        Route(isSuperAdmin: false, role: ProjectRole.ReadWrite);
        var nav = RenderNav();

        Expand(nav, "Solo");

        Assert.Contains(Links(nav), l => l.Contains("Add board"));
    }

    [Fact]
    public void Add_board_is_hidden_in_a_read_only_project()
    {
        Route(isSuperAdmin: false, role: ProjectRole.ReadWrite);
        var nav = RenderNav();

        // "Side quest" is Read-only for this user.
        Expand(nav, "Side quest");

        var links = Links(nav);
        Assert.DoesNotContain(links, l => l.Contains("Add board"));
    }

    [Fact]
    public void Instance_settings_is_super_admin_only()
    {
        Route(isSuperAdmin: false);
        Assert.DoesNotContain(Links(RenderNav()), l => l.Contains("Instance settings"));
    }
}

/// <summary>The store is the sidebar's source of truth; these pin its fetch rules.</summary>
public class ProjectNavStoreTests
{
    [Fact]
    public async Task Projects_are_fetched_once_however_many_callers_ask()
    {
        var http = new StubHttp();
        http.RouteJson("/api/projects", new[] { new ProjectSummaryDto(TestData.ProjectId, "Solo", null, null, ProjectRole.Read, false) });
        var store = new ProjectNavStore(http.CreateClient());

        await store.EnsureProjectsAsync();
        await store.EnsureProjectsAsync();

        Assert.Single(http.Paths, p => p.Contains("/api/projects"));
    }

    [Fact]
    public async Task Boards_are_cached_per_project()
    {
        var http = new StubHttp();
        http.RouteJson("/boards", new[] { new BoardDto(TestData.BoardId, TestData.ProjectId, "Main", null, null, true, 1) });
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
        http.RouteJson("/boards", new[] { new BoardDto(TestData.BoardId, TestData.ProjectId, "Main", null, null, true, 1) });
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
        http.RespondJson(new[] { new ProjectSummaryDto(TestData.ProjectId, "Solo", null, null, ProjectRole.Read, false) });
        var store = new ProjectNavStore(http.CreateClient());

        await store.EnsureProjectsAsync();
        Assert.Empty(store.Projects);

        await store.EnsureProjectsAsync();
        Assert.Single(store.Projects);
    }

    [Theory]
    [InlineData(false, ProjectRole.Read, false, false)]
    [InlineData(false, ProjectRole.ReadWrite, false, true)]
    [InlineData(false, ProjectRole.Read, true, true)]   // owner
    [InlineData(true, ProjectRole.Read, false, true)]   // super admin
    public void Add_board_permission_follows_the_APIs_write_rule(
        bool isSuperAdmin, ProjectRole role, bool isOwner, bool expected)
    {
        var project = new ProjectSummaryDto(TestData.ProjectId, "Solo", null, null, role, isOwner);

        Assert.Equal(expected, ProjectNavStore.CanAddBoard(project, isSuperAdmin));
    }
}
