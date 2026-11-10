using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
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

    private IRenderedComponent<InstanceSettings> Render(object? updates, params ExtensionRegistrationDto[] installed)
    {
        _http.RouteJson("api/settings", new InstanceSettingsDto(90, true, null, 1))
            .RouteJson("api/themes", new ThemeScanResultDto([], []))
            .RouteJson("api/extensions/admin",
                new ExtensionAdminDto(installed.Length > 0 ? [.. installed] : [Registration()], []))
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

    private static ExtensionRegistrationDto Registration(string id = "roadmap", string name = "Roadmap") =>
        new(id, name, "1.0.0", $"ext_{id}", "Loaded", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], false, [], null);

    private static ExtensionUpdateDto Available(string id, string name, string version = "1.1.0") =>
        new(id, name, "1.0.0", version, 1024, Compatible: true, null);

    private IEnumerable<string> Buttons(IRenderedComponent<InstanceSettings> page) =>
        page.FindAll("button").Select(b => b.TextContent.Trim());

    private InstanceRestart Restart => Services.GetRequiredService<InstanceRestart>();

    /// <summary>What the restart overlay will say once the app has reloaded.</summary>
    private InstanceRestart.Notice StoredNotice =>
        System.Text.Json.JsonSerializer.Deserialize<InstanceRestart.Notice>(
            (string)JSInterop.Invocations["sessionStorage.setItem"].Single().Arguments[1]!)!;

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

    [Fact]
    public void Update_all_counts_only_the_updates_that_can_be_applied()
    {
        var page = Render(
            Status(Available("roadmap", "Roadmap"),
                new ExtensionUpdateDto("mcp", "MCP", "1.0.0", "2.0.0", 1024, Compatible: false, "Needs a newer Solo.")),
            Registration(), Registration("mcp", "MCP"));

        page.WaitForAssertion(() => Assert.Contains("Update all (1)", Buttons(page)));
    }

    [Fact]
    public void Update_all_is_not_offered_when_nothing_can_be_updated()
    {
        var page = Render(Status());

        page.WaitForAssertion(() => Assert.Contains("ext_roadmap", page.Markup));
        Assert.DoesNotContain(Buttons(page), b => b.StartsWith("Update all", StringComparison.Ordinal));
    }

    /// <summary>
    /// The ids sent are the ones the operator was shown and confirmed, and the
    /// page hands over to the restart overlay rather than leaving a toast that
    /// asks them to reload by hand.
    /// </summary>
    [Fact]
    public async Task Update_all_confirms_installs_the_shown_updates_and_waits_out_the_restart()
    {
        _http.Route("api/updates/extensions", HttpStatusCode.OK,
            new ExtensionBulkUpdateResultDto(
                [new ExtensionUpdateResultDto("roadmap", "1.1.0", true), new ExtensionUpdateResultDto("mcp", "1.2.0", true)],
                [], Restarting: true),
            HttpMethod.Post);
        var page = Render(Status(Available("roadmap", "Roadmap"), Available("mcp", "MCP", "1.2.0")),
            Registration(), Registration("mcp", "MCP"));
        var dialogs = Render<MudDialogProvider>();

        page.WaitForAssertion(() => Assert.Contains("Update all (2)", Buttons(page)));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Contains("Update all")).Click());
        dialogs.WaitForAssertion(() => Assert.Contains("restarts this instance once", dialogs.Markup));
        Assert.False(Restart.InProgress);

        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Update all and restart").Click();

        page.WaitForAssertion(() => Assert.True(Restart.InProgress));
        var post = _http.Paths.IndexOf("POST /api/updates/extensions");
        Assert.Contains("\"roadmap\"", _http.Bodies[post]);
        Assert.Contains("\"mcp\"", _http.Bodies[post]);
        Assert.Equal(new InstanceRestart.Notice("Roadmap 1.1.0, MCP 1.2.0 installed.", Severity.Success), StoredNotice);
    }

    /// <summary>
    /// A failure is carried across the reload with the rest, since the page it
    /// would otherwise have been shown on is about to go.
    /// </summary>
    [Fact]
    public async Task A_partial_update_restarts_and_reports_what_failed_after_the_reload()
    {
        _http.Route("api/updates/extensions", HttpStatusCode.OK,
            new ExtensionBulkUpdateResultDto(
                [new ExtensionUpdateResultDto("roadmap", "1.1.0", true)],
                [new ExtensionUpdateFailureDto("mcp", "The download does not match the digest in the feed, so it was discarded.")],
                Restarting: true),
            HttpMethod.Post);
        var page = Render(Status(Available("roadmap", "Roadmap"), Available("mcp", "MCP")),
            Registration(), Registration("mcp", "MCP"));
        var dialogs = Render<MudDialogProvider>();

        page.WaitForAssertion(() => Assert.Contains("Update all (2)", Buttons(page)));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Contains("Update all")).Click());
        dialogs.WaitForAssertion(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Update all and restart").Click());

        page.WaitForAssertion(() => Assert.True(Restart.InProgress));
        Assert.StartsWith("Roadmap 1.1.0 installed. MCP wasn't updated: The download does not match",
            StoredNotice.Message, StringComparison.Ordinal);
        Assert.Equal(Severity.Warning, StoredNotice.Severity);
    }

    [Fact]
    public async Task An_update_that_installed_nothing_says_why_and_does_not_wait_for_a_restart()
    {
        _http.Route("api/updates/extensions", HttpStatusCode.OK,
            new ExtensionBulkUpdateResultDto([], [new ExtensionUpdateFailureDto("roadmap", "No feed names an update for roadmap.")],
                Restarting: false),
            HttpMethod.Post);
        var page = Render(Status(Available("roadmap", "Roadmap")));
        var dialogs = Render<MudDialogProvider>();

        page.WaitForAssertion(() => Assert.Contains("Update all (1)", Buttons(page)));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Contains("Update all")).Click());
        dialogs.WaitForAssertion(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Update all and restart").Click());

        page.WaitForAssertion(() => Assert.Contains("Update all (1)", Buttons(page)));
        Assert.False(Restart.InProgress);
    }

    [Fact]
    public async Task A_single_update_also_waits_out_the_restart()
    {
        _http.Route("api/updates/extensions/roadmap", HttpStatusCode.OK,
            new ExtensionUpdateResultDto("roadmap", "1.1.0", true), HttpMethod.Post);
        var page = Render(Status(Available("roadmap", "Roadmap")));
        var dialogs = Render<MudDialogProvider>();

        page.WaitForAssertion(() => Assert.Contains("Update", Buttons(page)));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Update").Click());
        dialogs.WaitForAssertion(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Update and restart").Click());

        page.WaitForAssertion(() => Assert.True(Restart.InProgress));
        Assert.Equal("Roadmap 1.1.0 installed.", StoredNotice.Message);
    }

    /// <summary>
    /// A proxy that gives up on a long batch answers for the instance, which
    /// carries on installing and then restarts. Taking that as an error would
    /// leave the page up for a restart nobody was told about.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_gateway_error_is_waited_out_as_a_restart(HttpStatusCode status)
    {
        _http.Route("api/updates/extensions", status, (object?)null, HttpMethod.Post);
        var page = Render(Status(Available("roadmap", "Roadmap")));
        var dialogs = Render<MudDialogProvider>();

        page.WaitForAssertion(() => Assert.Contains("Update all (1)", Buttons(page)));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Contains("Update all")).Click());
        dialogs.WaitForAssertion(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Update all and restart").Click());

        page.WaitForAssertion(() => Assert.True(Restart.InProgress));
        Assert.Empty(JSInterop.Invocations["sessionStorage.setItem"]);
    }

    private static ExtensionRegistrationDto WithFeeds(string? picked = null) =>
        Registration() with { UpdateFeeds = [new ExtensionUpdateFeedDto("beta", "Beta")], UpdateFeed = picked };

    private static IRenderedComponent<MudSelect<string>> FeedPicker(IRenderedComponent<InstanceSettings> page) =>
        page.FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == "Updates from");

    [Fact]
    public void A_package_with_one_feed_offers_no_choice()
    {
        var page = Render(Status());

        page.WaitForAssertion(() => Assert.Contains("ext_roadmap", page.Markup));
        Assert.DoesNotContain("Updates from", page.Markup);
    }

    [Fact]
    public void A_package_with_other_feeds_follows_the_default_until_one_is_picked()
    {
        var page = Render(Status(), WithFeeds());

        page.WaitForAssertion(() => Assert.Equal("", FeedPicker(page).Instance.Value));
        Assert.DoesNotContain("isn't offered by this version", page.Markup);
    }

    /// <summary>
    /// A newer package can drop a feed someone picked. The checker then reads
    /// the default, and the page has to say so rather than show a stale pick.
    /// </summary>
    [Fact]
    public void A_pick_the_package_no_longer_declares_is_shown_as_the_default()
    {
        var page = Render(Status(), WithFeeds(picked: "nightly"));

        page.WaitForAssertion(() => Assert.Contains("isn't offered by this version", page.Markup));
        Assert.Equal("", FeedPicker(page).Instance.Value);
    }

    [Fact]
    public async Task Picking_a_feed_saves_it_and_checks_again()
    {
        _http.Route("api/extensions/roadmap/update-feed", HttpStatusCode.NoContent, method: HttpMethod.Put);
        var page = Render(Status(), WithFeeds());
        page.WaitForAssertion(() => Assert.Single(_http.Paths, p => p.StartsWith("GET /api/updates")));

        await page.InvokeAsync(() => FeedPicker(page).Instance.ValueChanged.InvokeAsync("beta"));

        page.WaitForAssertion(() => Assert.Equal(2, _http.Paths.Count(p => p.StartsWith("GET /api/updates"))));
        Assert.Contains("PUT /api/extensions/roadmap/update-feed", _http.Paths);
        Assert.Contains(_http.Bodies, b => b.Contains("\"beta\""));
        Assert.Equal("beta", FeedPicker(page).Instance.Value);
    }
}
