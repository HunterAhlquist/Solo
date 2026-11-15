using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Extensions;
using Solo.Client.Kit;
using Solo.Shared.Dtos;
using Solo.Shared.Kit;

namespace Solo.Client.Tests;

/// <summary>
/// An extension's whole page on the kit surface: found by its address as the
/// router would find it, run with what the address gives it, and answered as
/// the app's to draw or the web's.
/// </summary>
public class KitPagesTests : ComponentTestBase
{
    public KitPagesTests() => Services.AddSingleton<ISoloKitRenderer, MudKitRenderer>();

    /// <summary>This test assembly stands in for an extension's client half.</summary>
    private static ExtensionCatalog Catalog() => new()
    {
        Loaded =
        [
            new LoadedExtension(
                new ClientExtensionDto("fixture", "Fixture", "1.0.0", ">=1.17 <2.0", "Fixture", []),
                Assembly.GetExecutingAssembly()),
        ],
    };

    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    private static KitPage? Resolve(string path) => KitPages.Resolve(path, Catalog(), Nothing);

    [Fact]
    public void A_page_is_found_by_its_address_and_handed_what_the_address_says()
    {
        var page = Resolve("fixture-kit/things/DMD");

        Assert.NotNull(page);
        Assert.Equal(typeof(KitThingsPage), page.Component);
        Assert.Equal("fixture", page.ExtensionId);
        Assert.Equal("DMD", page.Parameters["ProjectAlias"]);
        Assert.False(page.Web);
    }

    [Fact]
    public void An_address_is_read_as_the_router_reads_it()
    {
        // A leading slash, another case and an escaped segment are all the same address.
        Assert.Equal("my project", Resolve("/Fixture-Kit/Things/my%20project")?.Parameters["ProjectAlias"]);
        // Words of the template win over a parameter in the same place.
        Assert.Equal(typeof(KitNewThingPage), Resolve("fixture-kit/things/new")?.Component);
        // Too long, too short, and somewhere nobody has a page.
        Assert.Null(Resolve("fixture-kit/things/DMD/extra"));
        Assert.Null(Resolve("fixture-kit/things"));
        Assert.Null(Resolve("projects/DMD"));
    }

    [Fact]
    public void A_constrained_parameter_arrives_as_its_type_or_the_page_is_not_matched()
    {
        var id = Guid.NewGuid();

        var page = Resolve($"fixture-kit/thing/{id}");
        Assert.Equal(id, page?.Parameters["Id"]);
        // The optional one was left off, so it is not handed over at all.
        Assert.False(page!.Parameters.ContainsKey("Tab"));

        Assert.Equal("history", Resolve($"fixture-kit/thing/{id}/history")?.Parameters["Tab"]);
        Assert.Null(Resolve("fixture-kit/thing/not-an-id"));
    }

    [Fact]
    public void A_catch_all_takes_the_rest_of_the_address()
    {
        Assert.Equal("a/b/c.txt", Resolve("fixture-kit/files/a/b/c.txt")?.Parameters["Rest"]);
        Assert.False(Resolve("fixture-kit/files")!.Parameters.ContainsKey("Rest"));
    }

    [Fact]
    public void A_page_is_the_web_s_when_it_says_so_or_needs_a_query_string()
    {
        Assert.True(Resolve("fixture-kit/chart")?.Web);
        // Reads its query, which only the page the browser is at has.
        Assert.True(Resolve("fixture-kit/search")?.Web);
        // Handed one: a kit page it may be, but not one that could be given this.
        Assert.True(Resolve("fixture-kit/things/DMD?tab=2")?.Web);
        Assert.False(Resolve("fixture-kit/things/DMD#top")?.Web);
    }

    [Fact]
    public void An_extension_the_app_draws_itself_has_no_pages_here()
    {
        Assert.Null(KitPages.Resolve("fixture-kit/things/DMD", Catalog(), new HashSet<string> { "fixture" }));
        Assert.Empty(KitSeams.Find(KitSeams.Page, Catalog(), new HashSet<string> { "fixture" }));
        // With pages to draw, the surface is worth keeping open for them alone.
        Assert.Contains(KitSeams.Find(KitSeams.Page, Catalog(), Nothing), c => c.Component == typeof(KitThingsPage));
    }

    private IRenderedComponent<KitMountView> Mounted(string path, int revision = 0)
    {
        Services.AddSingleton(new StubHttp().CreateClient());
        Services.AddSingleton(Catalog());
        return Render<KitMountView>(parameters => parameters
            .Add(m => m.Mount, new KitMount("p1", KitSeams.Page, null, null, null, Path: path) { Revision = revision })
            .Add(m => m.Skip, Nothing));
    }

    [Fact]
    public void A_mounted_page_runs_with_its_address_and_is_marked_the_app_s_to_draw()
    {
        Services.AddSingleton(new StubHttp().CreateClient());
        Services.AddSingleton(Catalog());
        var mount = Render<CascadingValue<KitSurface>>(parameters => parameters
            .Add(c => c.Value, new KitSurface())
            .AddChildContent<KitMountView>(child => child
                .Add(m => m.Mount, new KitMount("p1", KitSeams.Page, null, null, null, Path: "fixture-kit/things/DMD"))
                .Add(m => m.Skip, Nothing)));

        var page = mount.Find("[data-kit-page]");
        Assert.Equal("native", page.GetAttribute("data-kit-page"));
        Assert.Equal("fixture", page.GetAttribute("data-extension"));
        Assert.Equal("Things of DMD", page.QuerySelector("solo-kit[data-kind=text]")?.GetAttribute("data-text"));
    }

    [Fact]
    public void A_page_that_is_the_web_s_is_said_to_be_and_is_not_run()
    {
        var chart = Mounted("fixture-kit/chart");

        var page = chart.Find("[data-kit-page]");
        Assert.Equal("web", page.GetAttribute("data-kit-page"));
        Assert.Equal("fixture", page.GetAttribute("data-extension"));
        Assert.Equal(0, page.ChildElementCount);
    }

    [Fact]
    public void An_address_no_extension_has_a_page_at_is_the_web_s_too()
    {
        // A page of core's own, which the web client has and the surface does not look for.
        var mount = Mounted("admin/audit");

        Assert.Equal("web", mount.Find("[data-kit-page]").GetAttribute("data-kit-page"));
    }

    [Fact]
    public void Reading_a_page_again_starts_it_over()
    {
        KitThingsPage.Started = 0;
        var mount = Mounted("fixture-kit/things/DMD");
        Assert.Equal(1, KitThingsPage.Started);

        mount.Render(parameters => parameters
            .Add(m => m.Mount, new KitMount("p1", KitSeams.Page, null, null, null, Path: "fixture-kit/things/DMD") { Revision = 1 }));

        Assert.Equal(2, KitThingsPage.Started);
    }
}

[Route("/fixture-kit/things/{ProjectAlias}")]
public sealed class KitThingsPage : ComponentBase
{
    /// <summary>How many of these have been started, for the one test that counts.</summary>
    public static int Started;

    [Parameter] public string ProjectAlias { get; set; } = "";

    protected override void OnInitialized() => Started++;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<SoloText>(0);
        builder.AddComponentParameter(1, nameof(SoloText.Text), $"Things of {ProjectAlias}");
        builder.CloseComponent();
    }
}

[Route("/fixture-kit/things/new")]
public sealed class KitNewThingPage : ComponentBase;

[Route("/fixture-kit/thing/{Id:guid}/{Tab?}")]
public sealed class KitThingPage : ComponentBase
{
    [Parameter] public Guid Id { get; set; }
    [Parameter] public string? Tab { get; set; }
}

[Route("/fixture-kit/files/{*Rest}")]
public sealed class KitFilesPage : ComponentBase
{
    [Parameter] public string? Rest { get; set; }
}

[Route("/fixture-kit/chart")]
[WebPage]
public sealed class KitChartPage : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "A chart.");
}

[Route("/fixture-kit/search")]
public sealed class KitSearchPage : ComponentBase
{
    [SupplyParameterFromQuery] public string? Q { get; set; }
}
