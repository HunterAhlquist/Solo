using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Extensions;
using Solo.Client.Pages;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The extension update affordance on Admin settings. An update offered here
/// ends in a restart of the instance, so what matters is that it is offered
/// only when it can actually be applied — and that the page survives the check
/// failing, which is the common case on an instance with no outbound access.
/// </summary>
public class ExtensionUpdateUiTests : ComponentTestBase
{
    private readonly StubHttp _http = new();

    private IRenderedComponent<InstanceSettings> Render(object? updates)
    {
        _http.RouteJson("api/settings", new InstanceSettingsDto(90, true, null, 1))
            .RouteJson("api/themes", new ThemeScanResultDto([], []))
            .RouteJson("api/extensions/admin", new ExtensionAdminDto([Registration()], []))
            .RouteJson("api/admin/users", new List<AdminUserDto>());

        // Null stands for an endpoint that answers with nothing, which is what
        // a failed check looks like to the page.
        if (updates is not null)
        {
            _http.RouteJson("api/updates", updates);
        }

        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new UserSession(api));
        Services.AddSingleton(new ExtensionCatalog());
        var page = Render<InstanceSettings>();
        page.WaitForAssertion(() => Assert.Contains("Extensions", page.Markup));
        return page;
    }

    private static ExtensionRegistrationDto Registration() =>
        new("roadmap", "Roadmap", "1.0.0", "ext_roadmap", "Loaded", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], false);

    private static UpdateStatusDto Status(params ExtensionUpdateDto[] extensions) =>
        new(Enabled: true, DateTimeOffset.UtcNow, null, null, extensions);

    [Fact]
    public void An_available_update_is_announced_with_its_size_and_offers_a_button()
    {
        var page = Render(Status(new ExtensionUpdateDto(
            "roadmap", "Roadmap", "1.0.0", "1.1.0", 2 * 1024 * 1024, Compatible: true, null)));

        page.WaitForAssertion(() => Assert.Contains("Version 1.1.0 is available", page.Markup));
        Assert.Contains("2 MB", page.Markup);
        Assert.Contains("Update", page.FindAll("button").Select(b => b.TextContent.Trim()));
    }

    /// <summary>
    /// An update this server could not load is stated as a blocker rather than
    /// offered: installing it would leave the extension failing to load at the
    /// next boot, which looks exactly like the update having broken it.
    /// </summary>
    [Fact]
    public void An_incompatible_update_explains_itself_and_offers_no_button()
    {
        var page = Render(Status(new ExtensionUpdateDto(
            "roadmap", "Roadmap", "1.0.0", "2.0.0", 1024, Compatible: false,
            "Version 2.0.0 needs a Solo server in the range >=2.0; this one implements 1.12.0.")));

        page.WaitForAssertion(() => Assert.Contains("needs a Solo server", page.Markup));
        Assert.DoesNotContain("Update", page.FindAll("button").Select(b => b.TextContent.Trim()));
    }

    [Fact]
    public void No_update_means_no_announcement_and_no_button()
    {
        var page = Render(Status());

        page.WaitForAssertion(() => Assert.Contains("ext_roadmap", page.Markup));
        Assert.DoesNotContain("is available", page.Markup);
        Assert.DoesNotContain("Update", page.FindAll("button").Select(b => b.TextContent.Trim()));
    }

    [Fact]
    public void A_disabled_check_says_so_rather_than_implying_the_instance_is_current()
    {
        var page = Render(new UpdateStatusDto(
            Enabled: false, null, null, null, []));

        page.WaitForAssertion(() => Assert.Contains("Update checks are disabled", page.Markup));
    }

    [Fact]
    public void A_failed_check_is_reported_without_claiming_the_instance_is_current()
    {
        var page = Render(new UpdateStatusDto(
            Enabled: true, DateTimeOffset.UtcNow, "The update check timed out.", null, []));

        page.WaitForAssertion(() => Assert.Contains("Couldn't check for updates", page.Markup));
        Assert.Contains("timed out", page.Markup);
    }

    /// <summary>
    /// The regression this guards: the update check was once awaited inside the
    /// same try block as the settings load, so an unreachable feed replaced the
    /// entire settings page with an error banner. The check is advisory and the
    /// page has to outlive it.
    /// </summary>
    [Fact]
    public void An_update_check_that_fails_outright_leaves_the_page_working()
    {
        var page = Render(updates: null);

        page.WaitForAssertion(() => Assert.Contains("ext_roadmap", page.Markup));
        Assert.Contains("Extensions", page.Markup);
        Assert.DoesNotContain("Couldn't load instance settings", page.Markup);
    }
}
