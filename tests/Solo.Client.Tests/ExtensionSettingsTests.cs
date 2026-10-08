using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Extensions;
using Solo.Client.Pages;
using Solo.Client.State;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// An extension's own settings, rendered inside its entry on Admin settings by
/// a component its client half marks <see cref="ExtensionSettingsAttribute"/>.
/// </summary>
public class ExtensionSettingsTests : ComponentTestBase
{
    private readonly StubHttp _http = new();

    private IRenderedComponent<InstanceSettings> Render(ExtensionAdminDto extensions, ExtensionCatalog catalog)
    {
        _http.RouteJson("api/settings", new InstanceSettingsDto(90, true, null, 1))
            .RouteJson("api/themes", new ThemeScanResultDto([], []))
            .RouteJson("api/extensions/admin", extensions)
            .RouteJson("api/admin/users", new List<AdminUserDto>());
        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new UserSession(api));
        Services.AddSingleton(catalog);
        var page = Render<InstanceSettings>();
        page.WaitForAssertion(() => Assert.Contains("Extensions", page.Markup));
        return page;
    }

    private static ExtensionRegistrationDto Registration(string id, string state = "Loaded") =>
        new(id, id, "1.0.0", $"ext_{id}", state, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], false);

    /// <summary>Each id's client half is this test assembly, which carries the panels below.</summary>
    private static ExtensionCatalog CatalogFor(params string[] ids) => new()
    {
        Loaded = ids.Select(id => new LoadedExtension(
            new ClientExtensionDto(id, id, "1.0.0", ">=1.3 <2.0", "Tests", []),
            Assembly.GetExecutingAssembly())).ToList(),
    };

    [Fact]
    public void A_loaded_extensions_panel_renders_in_its_own_entry_and_no_other()
    {
        var page = Render(new ExtensionAdminDto([Registration("alpha"), Registration("beta")], []),
            CatalogFor("alpha"));

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".test-settings-panel")));
        var panel = page.Find(".test-settings-panel");
        Assert.Equal("alpha", panel.TextContent);
        var entry = panel.Closest(".d-flex.align-center.py-2")!;
        Assert.Contains("ext_alpha", entry.TextContent);
        Assert.DoesNotContain("ext_beta", entry.TextContent);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Absent")]
    public void A_disabled_extension_or_one_whose_package_is_missing_gets_no_panel(string state)
    {
        var page = Render(new ExtensionAdminDto([Registration("alpha", state)], []), CatalogFor("alpha"));

        Assert.Empty(page.FindAll(".extension-settings"));
    }

    [Fact]
    public void A_panel_that_throws_shows_an_error_line_and_the_page_keeps_working()
    {
        var page = Render(new ExtensionAdminDto([Registration(FaultySettingsPanel.Trigger), Registration("alpha")], []),
            CatalogFor(FaultySettingsPanel.Trigger, "alpha"));

        page.WaitForAssertion(() => Assert.Contains("This extension's settings couldn't load.", page.Markup));
        Assert.Contains("Audit retention", page.Markup);
        Assert.Contains("alpha", page.FindAll(".test-settings-panel").Select(p => p.TextContent));
    }

    [Fact]
    public void The_page_renders_with_no_extensions_installed()
    {
        var page = Render(new ExtensionAdminDto([], []), new ExtensionCatalog());

        page.WaitForAssertion(() => Assert.Contains("No extensions installed.", page.Markup));
        Assert.Empty(page.FindAll(".extension-settings"));
    }

    /// <summary>Client halves load after sign-in, so the page may already be up when they arrive.</summary>
    [Fact]
    public void A_panel_appears_once_its_assembly_loads()
    {
        var catalog = new ExtensionCatalog();
        var page = Render(new ExtensionAdminDto([Registration("alpha")], []), catalog);
        page.WaitForAssertion(() => Assert.Contains("ext_alpha", page.Markup));
        Assert.Empty(page.FindAll(".test-settings-panel"));

        catalog.Apply(CatalogFor("alpha").Loaded, []);

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".test-settings-panel")));
    }
}

/// <summary>A settings panel that reports which extension it was rendered for.</summary>
[ExtensionSettings]
public sealed class TestSettingsPanel : ComponentBase
{
    [Parameter] public string ExtensionId { get; set; } = "";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", "test-settings-panel");
        builder.AddContent(2, ExtensionId);
        builder.CloseElement();
    }
}

/// <summary>Throws for one extension id only, so it can share an assembly with a panel that works.</summary>
[ExtensionSettings]
public sealed class FaultySettingsPanel : ComponentBase
{
    public const string Trigger = "faulty";

    [Parameter] public string ExtensionId { get; set; } = "";

    protected override void OnParametersSet()
    {
        if (ExtensionId == Trigger)
        {
            throw new InvalidOperationException("This panel failed on purpose.");
        }
    }
}
