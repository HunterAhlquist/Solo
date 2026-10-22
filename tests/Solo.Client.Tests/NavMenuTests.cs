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

/// <summary>
/// The sidebar is the app's map: projects, and each project's open boards on
/// demand. Boards must not be fetched until a project is expanded — otherwise
/// signing in costs one request per project for a tree nobody opened.
/// </summary>
public class NavMenuTests : ComponentTestBase
{
    private static readonly Guid OtherProjectId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private readonly StubHttp _http = new();

    public NavMenuTests()
    {
        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new UserSession(api));
        Services.AddSingleton(new ProjectNavStore(api));
        Services.AddScoped<RecentProjectsStore>();
        Services.AddScoped<ProjectOrderStore>();
        Services.AddScoped<DragBridge>();
        // The sidebar renders extension nav entries. An empty catalog is the
        // normal case — no extensions installed — and keeps these tests about
        // the core tree; ExtensionNavTests covers the entries themselves.
        Services.AddSingleton(new ExtensionCatalog());
    }


    private static ProjectSummaryDto Project(Guid id, string name, ProjectRole role, bool isOwner) =>
        new(id, name, AliasOf(name), "Games", null, role, isOwner);

    private static BoardDto Board(Guid id, string name, bool isOpen = true) =>
        new(id, TestData.ProjectId, name, AliasOf(name), "Rocket", null, isOpen, 1);

    /// <summary>"Side quest" → "SIDEQUES": a valid alias derived from the display name.</summary>
    private static string AliasOf(string name)
    {
        var upper = new string(name.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return upper[..Math.Min(upper.Length, 8)];
    }

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
    public void A_board_link_points_at_the_aliased_board_route()
    {
        Route(isSuperAdmin: false);
        var nav = RenderNav();

        Expand(nav, "Solo");

        var href = nav.FindAll("a.mud-nav-link")
            .First(a => a.TextContent.Contains("Main"))
            .GetAttribute("href");

        Assert.Equal("projects/SOLO/boards/MAIN", href);
    }

    // -- Project ordering ---------------------------------------------------

    /// <summary>
    /// The gesture itself is in js/drag.js, out of a test's reach. What a test
    /// can hold onto is the contract between the two: the attributes the script
    /// looks for, and the drop it hands back.
    /// </summary>
    [Fact]
    public void Each_project_is_a_drag_item_dragged_by_its_icon()
    {
        Route(isSuperAdmin: false);

        var nav = RenderNav();

        Assert.Equal("project", nav.Find("[data-drag-zone='projects']").GetAttribute("data-drag-group"));
        Assert.Equal(2, nav.FindAll(".project-nav__entity-icon").Count);
        Assert.Equal(2, nav.FindAll(".project-nav__drag-indicator").Count);
        Assert.All(nav.FindAll(".project-nav__drag-handle"), handle =>
            Assert.True(handle.HasAttribute("data-drag-handle")));

        Assert.All(nav.FindAll("[data-drag-item]"), item =>
        {
            Assert.Equal(".project-nav__drag-handle", item.GetAttribute("data-drag-from"));

            // The whole group would be a preview the size of the sidebar; what
            // rides under the pointer is the title row alone.
            var preview = item.QuerySelector(item.GetAttribute("data-drag-preview")!);
            Assert.NotNull(preview);
            Assert.NotNull(preview.QuerySelector(".project-nav__name"));
            Assert.Empty(preview.QuerySelectorAll(".mud-nav-link"));
        });
    }

    [Fact]
    public async Task Dropping_a_project_after_the_last_one_moves_the_whole_project_group()
    {
        Route(isSuperAdmin: false);
        var nav = RenderNav();

        await DropAsync(nav, TestData.ProjectId, index: 1);

        Assert.Equal(["Side quest", "Solo"],
            nav.FindAll(".project-nav__name").Select(name => name.TextContent.Trim()));
        var persisted = JSInterop.VerifyInvoke("solo.storage.set");
        Assert.Equal("solo.projectOrder", persisted.Arguments[0]);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(new[] { OtherProjectId, TestData.ProjectId }),
            persisted.Arguments[1]);
    }

    /// <summary>A drop naming a project the sidebar isn't showing changes nothing.</summary>
    [Fact]
    public async Task Dropping_an_unknown_project_reorders_nothing()
    {
        Route(isSuperAdmin: false);
        var nav = RenderNav();

        await DropAsync(nav, Guid.NewGuid(), index: 0);

        Assert.Equal(["Solo", "Side quest"],
            nav.FindAll(".project-nav__name").Select(name => name.TextContent.Trim()));
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.storage.set");
    }

    /// <summary>Hands the sidebar the drop js/drag.js would have reported.</summary>
    private Task DropAsync(IRenderedComponent<NavMenu> nav, Guid projectId, int index) =>
        nav.InvokeAsync(() => Services.GetRequiredService<DragBridge>()
            .OnDropAsync(new DragDrop("project", projectId.ToString(), "projects", index)));

    [Fact]
    public void A_saved_manual_order_wins_over_the_API_order_on_the_next_load()
    {
        JSInterop.Setup<string?>("solo.storage.get", "solo.projectOrder")
            .SetResult(System.Text.Json.JsonSerializer.Serialize(
                new[] { OtherProjectId, TestData.ProjectId }));
        Route(isSuperAdmin: false);

        var nav = RenderNav();

        nav.WaitForAssertion(() => Assert.Equal(["Side quest", "Solo"],
            nav.FindAll(".project-nav__name").Select(name => name.TextContent.Trim())));
    }

    // -- Create affordances --------------------------------------------------

    [Fact]
    public void Add_project_is_offered_to_a_super_admin()
    {
        Route(isSuperAdmin: true);

        var nav = RenderNav();
        Assert.Contains(Links(nav), l => l.Contains("Add project"));

        var footer = nav.Find(".nav-shell__footer").InnerHtml;
        Assert.True(
            footer.IndexOf("Add project", StringComparison.Ordinal) <
            footer.IndexOf("mud-divider", StringComparison.Ordinal));
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
    public void Add_board_is_offered_to_a_super_admin_in_every_project()
    {
        Route(isSuperAdmin: true, role: ProjectRole.ReadWrite);
        var nav = RenderNav();

        // "Side quest" is Read-only for this user: the lifecycle rule doesn't
        // look at the project role.
        Expand(nav, "Solo");
        Expand(nav, "Side quest");

        Assert.Equal(2, Links(nav).Count(l => l.Contains("Add board")));
    }

    [Fact]
    public void Add_board_is_hidden_from_everyone_else_even_an_owner()
    {
        // The API reserves board creation for the super admin; don't offer a
        // button that will 403.
        Route(isSuperAdmin: false, role: ProjectRole.ReadWrite, isOwner: true);
        var nav = RenderNav();

        Expand(nav, "Solo");

        nav.WaitForAssertion(() => Assert.Contains(Links(nav), l => l.Contains("Main")));
        Assert.DoesNotContain(Links(nav), l => l.Contains("Add board"));
    }

    [Fact]
    public void Admin_settings_is_super_admin_only()
    {
        Route(isSuperAdmin: false);
        Assert.DoesNotContain(Links(RenderNav()), l => l.Contains("Admin settings"));
    }
}
