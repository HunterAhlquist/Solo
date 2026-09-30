using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Extensions;
using Solo.Client.Layout;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// A project group's links come in labelled sections: Boards first, then one
/// per <see cref="ProjectNavSectionAttribute"/> component a loaded extension
/// contributes, each under its own header and its own error boundary.
/// </summary>
public class ProjectNavSectionTests : ComponentTestBase
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly StubHttp _http = new();

    public ProjectNavSectionTests()
    {
        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new UserSession(api));
        Services.AddSingleton(new ProjectNavStore(api));
        Services.AddScoped<RecentProjectsStore>();
        Services.AddScoped<ProjectOrderStore>();
        Services.AddScoped<DragBridge>();
    }

    [Fact]
    public void Boards_and_Add_board_sit_under_a_Boards_header()
    {
        Services.AddSingleton(new ExtensionCatalog());
        Seed("Alpha", ProjectRole.ReadWrite);

        var nav = Render<NavMenu>();
        Expand(nav, "Alpha");

        Assert.Equal(["Boards"], Headers(nav));
        var order = LinkTexts(nav);
        Assert.True(order.IndexOf("Main") < order.IndexOf("Add board"));
        Assert.True(order.IndexOf("Add board") < order.IndexOf("Project settings"));
    }

    [Fact]
    public void A_contributed_section_renders_under_its_own_header_after_Boards()
    {
        Services.AddSingleton(CatalogWithTestSections());
        Seed("Alpha", ProjectRole.ReadWrite);

        var nav = Render<NavMenu>();
        Expand(nav, "Alpha");

        // Ordered by the attribute's Order, not by type or declaration order.
        Assert.Equal(["Boards", "Timelines", "Faulty"], Headers(nav));
        Assert.Equal("ALPHA:True", nav.Find(".test-nav-section").TextContent);
        var markup = nav.Markup;
        Assert.True(markup.IndexOf(">Timelines<", StringComparison.Ordinal) <
                    markup.IndexOf("test-nav-section", StringComparison.Ordinal));
        Assert.True(markup.IndexOf("test-nav-section", StringComparison.Ordinal) <
                    markup.IndexOf("Project settings", StringComparison.Ordinal));
    }

    [Fact]
    public void A_read_only_member_gets_a_section_that_cannot_write()
    {
        Services.AddSingleton(CatalogWithTestSections());
        Seed("Alpha", ProjectRole.Read);

        var nav = Render<NavMenu>();
        Expand(nav, "Alpha");

        Assert.Equal("ALPHA:False", nav.Find(".test-nav-section").TextContent);
    }

    [Fact]
    public void A_section_that_throws_costs_one_line_and_the_rest_of_the_nav_keeps_working()
    {
        Services.AddSingleton(CatalogWithTestSections());
        Seed(FaultySection.Trigger, ProjectRole.ReadWrite);

        var nav = Render<NavMenu>();
        Expand(nav, FaultySection.Trigger);

        Assert.Contains("Faulty couldn't load.", nav.Markup);
        Assert.Contains("Main", LinkTexts(nav));
        Assert.Contains("Project settings", LinkTexts(nav));
        Assert.NotEmpty(nav.FindAll(".test-nav-section"));
    }

    /// <summary>Extension assemblies load after sign-in, so a sidebar already on screen has to pick them up.</summary>
    [Fact]
    public void Sections_appear_once_the_extension_assemblies_load()
    {
        var catalog = new ExtensionCatalog();
        Services.AddSingleton(catalog);
        Seed("Alpha", ProjectRole.ReadWrite);
        var nav = Render<NavMenu>();
        Expand(nav, "Alpha");
        Assert.Equal(["Boards"], Headers(nav));

        catalog.Apply(CatalogWithTestSections().Loaded, []);

        nav.WaitForAssertion(() => Assert.Contains("Timelines", Headers(nav)));
    }

    private static ExtensionCatalog CatalogWithTestSections() => new()
    {
        Loaded =
        [
            new LoadedExtension(
                new ClientExtensionDto("fixture", "Fixture", "1.0.0", ">=1.3 <2.0", "Fixture", []),
                Assembly.GetExecutingAssembly()),
        ],
    };

    private void Seed(string name, ProjectRole role)
    {
        _http.RouteJson($"api/projects/{ProjectId}/boards",
            new[] { new BoardDto(TestData.BoardId, ProjectId, "Main", "MAIN", "Rocket", null, true, 1) });
        _http.RouteJson("api/projects", new[]
        {
            new ProjectSummaryDto(ProjectId, name, name.ToUpperInvariant(), "Games", null, role, false),
        });
    }

    private static List<string> Headers(IRenderedComponent<NavMenu> nav) =>
        nav.FindAll(".nav-section-header__caption").Select(h => h.TextContent.Trim()).ToList();

    private static List<string> LinkTexts(IRenderedComponent<NavMenu> nav) =>
        nav.FindAll(".mud-nav-link").Select(l => l.TextContent.Trim()).ToList();

    private static void Expand(IRenderedComponent<NavMenu> nav, string projectName) =>
        nav.FindAll("button.mud-nav-link").First(b => b.TextContent.Contains(projectName)).Click();
}

/// <summary>A contributed section that reports the parameters it was given.</summary>
[ProjectNavSection("Timelines", Order = 10)]
public sealed class TimelinesSection : ComponentBase
{
    [Parameter] public ProjectSummaryDto Project { get; set; } = null!;
    [Parameter] public bool CanWrite { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", "test-nav-section");
        builder.AddContent(2, $"{Project.Alias}:{CanWrite}");
        builder.CloseElement();
    }
}

/// <summary>
/// Throws only for one project, so other suites that load this assembly as a
/// stand-in extension don't render an error into every sidebar.
/// </summary>
[ProjectNavSection("Faulty", Order = 20)]
public sealed class FaultySection : ComponentBase
{
    public const string Trigger = "Faulty";

    [Parameter] public ProjectSummaryDto Project { get; set; } = null!;
    [Parameter] public bool CanWrite { get; set; }

    protected override void OnParametersSet()
    {
        if (Project.Name == Trigger)
        {
            throw new InvalidOperationException("This section failed on purpose.");
        }
    }
}
