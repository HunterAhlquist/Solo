using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Solo.Client.Extensions;
using Solo.Client.Layout;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// Extension entries in the sidebar. The gating matters more than the
/// rendering: an entry whose assembly did not load would point at a route the
/// router cannot resolve, which reads to a user as a bug rather than as a
/// version mismatch.
/// </summary>
public class ExtensionNavTests : BunitContext, IAsyncLifetime
{
    private readonly StubHttp _http = new();

    public ExtensionNavTests()
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

    [Fact]
    public async Task A_loaded_extension_contributes_a_nav_entry()
    {
        Services.AddSingleton(CatalogWith(Nav("/roadmap/{projectAlias}", "Roadmap", "Read")));
        await SeedProjectAsync(ProjectRole.Read, isOwner: false);

        var nav = Render<NavMenu>();
        Expand(nav, "Alpha");

        Assert.Contains("Roadmap", nav.Markup);
        // {projectAlias} is substituted, so the link is per project.
        Assert.Contains("roadmap/ALPHA", nav.Markup);
    }

    [Fact]
    public async Task A_write_only_entry_is_hidden_from_a_read_only_member()
    {
        Services.AddSingleton(CatalogWith(Nav("/planner/{projectAlias}", "Planner", "Write")));
        await SeedProjectAsync(ProjectRole.Read, isOwner: false);

        var nav = Render<NavMenu>();
        Expand(nav, "Alpha");

        Assert.DoesNotContain("Planner", nav.Markup);
    }

    [Fact]
    public async Task An_owner_only_entry_is_hidden_from_a_non_owner()
    {
        Services.AddSingleton(CatalogWith(Nav("/billing/{projectAlias}", "Billing", "Owner")));
        await SeedProjectAsync(ProjectRole.ReadWrite, isOwner: false);

        var nav = Render<NavMenu>();
        Expand(nav, "Alpha");

        Assert.DoesNotContain("Billing", nav.Markup);
    }

    /// <summary>
    /// The case the Electron shell makes real: the server advertises an
    /// extension whose client half this build cannot load. Nothing about it may
    /// reach the sidebar.
    /// </summary>
    [Fact]
    public async Task An_extension_that_failed_to_load_contributes_nothing()
    {
        Services.AddSingleton(new ExtensionCatalog
        {
            Skipped = [new SkippedExtension("roadmap", "Needs Solo client API >=2.0")],
        });
        await SeedProjectAsync(ProjectRole.ReadWrite, isOwner: true);

        var nav = Render<NavMenu>();
        Expand(nav, "Alpha");

        Assert.DoesNotContain("Roadmap", nav.Markup);
    }

    /// <summary>
    /// An entry whose route names no project is instance-level: one of it, in
    /// the footer beside Admin settings, rather than one per project. This is
    /// what lets an extension ship a page that is not about a single project —
    /// the MCP access page is the shipped example.
    /// </summary>
    [Fact]
    public async Task An_entry_with_no_project_in_its_route_sits_in_the_footer()
    {
        Services.AddSingleton(CatalogWith(Nav("/mcp", "MCP access", "Read")));
        await SeedProjectAsync(ProjectRole.Read, isOwner: false);

        var nav = Render<NavMenu>();

        // Present without expanding any project…
        Assert.Contains("MCP access", nav.Markup);
        Assert.Contains("href=\"mcp\"", nav.Markup);

        // …and in the footer, not inside the project tree.
        var footer = nav.Find(".nav-shell__footer");
        Assert.Contains("MCP access", footer.TextContent);
    }

    /// <summary>
    /// Instance-level entries appear once, however many projects the viewer has —
    /// the substitution that makes a project entry per-project does not apply.
    /// </summary>
    [Fact]
    public async Task An_instance_level_entry_appears_once_per_sidebar()
    {
        Services.AddSingleton(CatalogWith(Nav("/mcp", "MCP access", "Read")));
        await SeedProjectsAsync();

        var nav = Render<NavMenu>();

        Assert.Single(nav.FindAll("a[href='mcp']"));
    }

    private static ClientExtensionNavDto Nav(string route, string label, string requires) =>
        new(route, label, null, requires);

    private static ExtensionCatalog CatalogWith(params ClientExtensionNavDto[] nav) => new()
    {
        Loaded =
        [
            new LoadedExtension(
                new ClientExtensionDto("fixture", "Fixture", "1.0.0", ">=1.0 <2.0", "Fixture", nav),
                Assembly.GetExecutingAssembly()),
        ],
    };

    private async Task SeedProjectAsync(ProjectRole role, bool isOwner)
    {
        var projectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        _http.RouteJson($"api/projects/{projectId}/boards", Array.Empty<BoardDto>());
        _http.RouteJson("api/projects", new[]
        {
            new ProjectSummaryDto(projectId, "Alpha", "ALPHA", "Games", null, role, isOwner),
        });
        await Task.CompletedTask;
    }

    private async Task SeedProjectsAsync()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        _http.RouteJson($"api/projects/{first}/boards", Array.Empty<BoardDto>());
        _http.RouteJson($"api/projects/{second}/boards", Array.Empty<BoardDto>());
        _http.RouteJson("api/projects", new[]
        {
            new ProjectSummaryDto(first, "Alpha", "ALPHA", "Games", null, ProjectRole.ReadWrite, true),
            new ProjectSummaryDto(second, "Beta", "BETA", "Games", null, ProjectRole.ReadWrite, true),
        });
        await Task.CompletedTask;
    }

    private static void Expand(IRenderedComponent<NavMenu> nav, string projectName) =>
        nav.FindAll("button.mud-nav-link")
            .First(b => b.TextContent.Contains(projectName))
            .Click();
}

/// <summary>
/// The compat check itself. This is the whole reason the client re-checks a
/// range the server already validated: in the Electron shell the bundled client
/// and the instance version independently.
/// </summary>
public class ExtensionCompatibilityTests
{
    [Theory]
    [InlineData(">=1.0 <2.0", true)]
    [InlineData(">=2.0 <3.0", false)]
    public void A_range_is_checked_against_this_builds_contract(string range, bool expected)
    {
        Assert.True(VersionRange.TryParse(range, out var parsed, out _));
        Assert.Equal(expected, parsed.Includes(ContractVersion.Parsed));
    }

    /// <summary>
    /// The rule a minor bump exists to enforce: a package that needs a member
    /// this build does not have is refused, and one whose floor this build meets
    /// loads.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="ContractVersion"/> rather than written as
    /// literals, so bumping the contract does not turn this into a chore — the
    /// property being asserted is the same at every version.
    /// </remarks>
    [Fact]
    public void A_package_needing_a_newer_contract_than_this_build_is_refused()
    {
        var current = ContractVersion.Parsed;

        Assert.True(VersionRange.TryParse(
            $">={current.Major}.{current.Minor} <{current.Major + 1}.0", out var met, out _));
        Assert.True(met.Includes(current), "a floor this build meets should load");

        Assert.True(VersionRange.TryParse(
            $">={current.Major}.{current.Minor + 1} <{current.Major + 1}.0", out var unmet, out _));
        Assert.False(unmet.Includes(current), "a floor one minor above this build should be refused");
    }
}
