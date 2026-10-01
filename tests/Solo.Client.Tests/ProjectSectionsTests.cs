using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Extensions;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// Extensions add sections to a project's home page and surfaces to its card on
/// All projects, each under its own title and its own error boundary.
/// </summary>
public class ProjectSectionsTests : ComponentTestBase
{
    private readonly StubHttp _http = new();

    public ProjectSectionsTests()
    {
        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new UserSession(api));
        Services.AddSingleton(new ProjectNavStore(api));
        Services.AddScoped<RecentProjectsStore>();
    }

    private static ExtensionCatalog Catalog() => new()
    {
        Loaded =
        [
            new LoadedExtension(
                new ClientExtensionDto("fixture", "Fixture", "1.0.0", ">=1.4 <2.0", "Fixture", []),
                Assembly.GetExecutingAssembly()),
        ],
    };

    private void RouteHome(string projectName, ProjectRole role)
    {
        var member = TestData.Member(TestData.MeId, "Hunter") with { IsOwner = false, Role = role };
        _http.RouteJson("/api/users/me", new UserDto(TestData.MeId, "h@x.io", "Hunter", null, false));
        _http.RouteJson("/members", new[] { member });
        _http.RouteJson("/boards", Array.Empty<BoardDto>());
        _http.RouteJson("/labels", Array.Empty<LabelDto>());
        _http.RouteJson("/api/projects/by-alias/SOLO",
            new ProjectDto(TestData.ProjectId, projectName, "SOLO", null, null, 1, [member]));
    }

    private IRenderedComponent<Pages.ProjectHome> RenderHome() =>
        Render<Pages.ProjectHome>(p => p.Add(x => x.ProjectAlias, "SOLO"));

    private static List<string> PageHeaders(IRenderedComponent<Pages.ProjectHome> page) =>
        page.FindAll(".page-section-header__title").Select(h => h.TextContent.Trim()).ToList();

    [Fact]
    public void Without_contributions_the_tab_is_plain_Boards_with_no_headers()
    {
        Services.AddSingleton(new ExtensionCatalog());
        RouteHome("Solo", ProjectRole.ReadWrite);

        var page = RenderHome();

        page.WaitForAssertion(() => Assert.Contains("Boards", page.Find(".mud-tab").TextContent));
        Assert.Empty(PageHeaders(page));
    }

    [Fact]
    public void A_home_section_gets_a_header_after_Boards_and_the_tab_names_both()
    {
        Services.AddSingleton(Catalog());
        RouteHome("Solo", ProjectRole.ReadWrite);

        var page = RenderHome();

        page.WaitForAssertion(() => Assert.Equal(["Boards", "Plans", "Broken"], PageHeaders(page)));
        Assert.Equal("Boards, Plans & Broken", page.Find(".mud-tab").TextContent.Trim());
        Assert.Equal("SOLO:True", page.Find(".test-home-section").TextContent);
    }

    [Fact]
    public void A_reader_gets_a_home_section_that_cannot_write()
    {
        Services.AddSingleton(Catalog());
        RouteHome("Solo", ProjectRole.Read);

        var page = RenderHome();

        page.WaitForAssertion(() => Assert.Equal("SOLO:False", page.Find(".test-home-section").TextContent));
    }

    [Fact]
    public void A_home_section_that_throws_costs_one_line()
    {
        Services.AddSingleton(Catalog());
        RouteHome(FaultyHomeSection.Trigger, ProjectRole.ReadWrite);

        var page = RenderHome();

        page.WaitForAssertion(() => Assert.Contains("Broken couldn't load.", page.Markup));
        Assert.NotEmpty(page.FindAll(".test-home-section"));
    }

    [Fact]
    public void A_card_section_is_its_own_titled_surface_and_Boards_gets_a_title_too()
    {
        Services.AddSingleton(Catalog());
        _http.RouteJson("/api/users/me", new UserDto(TestData.MeId, "h@x.io", "Hunter", null, false));
        _http.RouteJson("/boards", Array.Empty<BoardDto>());
        _http.RouteJson("/api/projects", new[]
        {
            new ProjectSummaryDto(TestData.ProjectId, "Solo", "SOLO", null, null, ProjectRole.Read, false),
        });

        var page = Render<Pages.Projects>();

        page.WaitForAssertion(() => Assert.Equal(
            ["Boards", "Plans"],
            page.FindAll(".project-card__surface-title").Select(t => t.TextContent.Trim())));
        Assert.Equal(2, page.FindAll(".project-card__boards").Count);
        Assert.Equal("SOLO:False", page.Find(".test-card-section").TextContent);
    }
}

/// <summary>A home section that reports the parameters it was given.</summary>
[ProjectHomeSection("Plans", Order = 10)]
public sealed class PlansHomeSection : ComponentBase
{
    [Parameter] public ProjectSummaryDto Project { get; set; } = null!;
    [Parameter] public bool CanWrite { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", "test-home-section");
        builder.AddContent(2, $"{Project.Alias}:{CanWrite}");
        builder.CloseElement();
    }
}

/// <summary>Throws only for one project, so a catalog of this assembly stays quiet elsewhere.</summary>
[ProjectHomeSection("Broken", Order = 20)]
public sealed class FaultyHomeSection : ComponentBase
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

/// <summary>A card section that reports the parameters it was given.</summary>
[ProjectCardSection("Plans")]
public sealed class PlansCardSection : ComponentBase
{
    [Parameter] public ProjectSummaryDto Project { get; set; } = null!;
    [Parameter] public bool CanWrite { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", "test-card-section");
        builder.AddContent(2, $"{Project.Alias}:{CanWrite}");
        builder.CloseElement();
    }
}
