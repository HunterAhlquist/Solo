using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Components;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The version panel on Admin settings. The check behind it is cached for hours
/// on the server, so the button that forces a fresh read is the only way an
/// operator can tell a stale answer from a current one — which is what most of
/// these cover.
/// </summary>
public class UpdatePanelTests : ComponentTestBase
{
    private StubHttp _http = null!;

    private IRenderedComponent<UpdatePanel> Render(Func<object?> updates)
    {
        _http = new StubHttp()
            .RouteJson("api/config/version", new InstanceVersionDto("1.12.0", "1.16.6+c194865"))
            .Route("api/updates", () => (System.Net.HttpStatusCode.OK, updates()));

        var api = _http.CreateClient();
        Services.AddSingleton(api);
        return Render<UpdatePanel>();
    }

    private static UpdateStatusDto Current() =>
        new(Enabled: true, DateTimeOffset.UtcNow, null, null, []);

    private static UpdateStatusDto Available() => new(
        Enabled: true, DateTimeOffset.UtcNow, null,
        new ServerUpdateDto(
            "1.16.6+c194865", "1.18.0",
            "https://github.com/HunterAhlquist/Solo/releases/tag/v1.18.0",
            "ghcr.io/hunterahlquist/solo:1.18.0",
            DateTimeOffset.UtcNow),
        []);

    [Fact]
    public void ShowsWhatTheInstanceIsRunningWithoutTheCommit()
    {
        var page = Render(() => Current());

        // The commit is useful in a bug report and noise in a heading.
        page.WaitForAssertion(() => Assert.Contains("1.16.6", page.Markup));
        Assert.DoesNotContain("c194865", page.Markup);
        Assert.Contains("1.12.0", page.Markup);
        Assert.Contains("up to date", page.Markup);
    }

    [Fact]
    public void ShowsTheAvailableVersionAndLinksToTheRelease()
    {
        var page = Render(() => Available());

        page.WaitForAssertion(() => Assert.Contains("Update available", page.Markup));
        Assert.Contains("1.18.0", page.Markup);
        // The image tag belongs to whoever redeploys the container, not to this
        // page; it was a field here once and only invited retyping a tag by hand.
        Assert.DoesNotContain("ghcr.io", page.Markup);

        var link = page.FindAll("a").Single(a => a.TextContent.Contains("Release notes"));
        Assert.Equal(
            "https://github.com/HunterAhlquist/Solo/releases/tag/v1.18.0",
            link.GetAttribute("href"));
    }

    /// <summary>
    /// The button has to ask the server to re-read the feed, not re-serve the
    /// cached answer — otherwise it is a no-op that looks like a check.
    /// </summary>
    [Fact]
    public void CheckingForUpdatesForcesARefresh()
    {
        var page = Render(() => Current());
        page.WaitForAssertion(() => Assert.Contains("up to date", page.Markup));
        Assert.DoesNotContain(_http.Paths, path => path.Contains("refresh=true"));

        page.FindAll("button").Single(b => b.TextContent.Contains("Check for updates")).Click();

        page.WaitForAssertion(() =>
            Assert.Contains(_http.Paths, path => path.Contains("api/updates?refresh=true")));
    }

    /// <summary>A forced check that finds something must redraw, not just refetch.</summary>
    [Fact]
    public void AForcedCheckShowsWhatItFound()
    {
        var found = false;
        var page = Render(() => found ? Available() : Current());
        page.WaitForAssertion(() => Assert.Contains("up to date", page.Markup));

        found = true;
        page.FindAll("button").Single(b => b.TextContent.Contains("Check for updates")).Click();

        page.WaitForAssertion(() => Assert.Contains("Update available", page.Markup));
        Assert.Contains("1.18.0", page.Markup);
    }

    [Fact]
    public void ReportsAFailedCheckRatherThanClaimingToBeCurrent()
    {
        var page = Render(() => new UpdateStatusDto(
            Enabled: true, DateTimeOffset.UtcNow, "The update check timed out.", null, []));

        page.WaitForAssertion(() => Assert.Contains("Couldn't check for updates", page.Markup));
        Assert.DoesNotContain("up to date", page.Markup);
    }

    /// <summary>
    /// A forced check that throws has no status to carry an error, and the panel
    /// must not go on showing the previous answer. Reporting "no updates" for a
    /// check that never completed is the exact confusion this panel exists to
    /// prevent.
    /// </summary>
    [Fact]
    public void AForcedCheckThatFailsSaysSoInsteadOfKeepingTheOldAnswer()
    {
        var fail = false;
        var page = Render(() => fail ? null : Current());
        page.WaitForAssertion(() => Assert.Contains("up to date", page.Markup));

        fail = true;
        page.FindAll("button").Single(b => b.TextContent.Contains("Check for updates")).Click();

        page.WaitForAssertion(() => Assert.Contains("Couldn't check for updates", page.Markup));
        Assert.DoesNotContain("up to date", page.Markup);
    }

    [Fact]
    public void SaysWhenChecksAreDisabled()
    {
        var page = Render(() => new UpdateStatusDto(Enabled: false, null, null, null, []));

        page.WaitForAssertion(() => Assert.Contains("Update checks are disabled", page.Markup));
        Assert.DoesNotContain("up to date", page.Markup);
    }

    /// <summary>
    /// Extension updates are applied further down the page, but an operator
    /// should not have to scroll to find out any exist.
    /// </summary>
    [Fact]
    public void PointsAtExtensionUpdatesWithoutOfferingThemHere()
    {
        var page = Render(() => new UpdateStatusDto(
            Enabled: true, DateTimeOffset.UtcNow, null, null,
            [new ExtensionUpdateDto("roadmap", "Roadmap", "1.0.0", "1.1.0", 2048, true, null)]));

        page.WaitForAssertion(() => Assert.Contains("1 extension update", page.Markup));
        Assert.DoesNotContain("Update and restart", page.Markup);
    }

    /// <summary>
    /// The instance's own version comes from a different endpoint than the
    /// check, so it survives a feed that cannot be reached at all.
    /// </summary>
    [Fact]
    public void StillReportsTheRunningVersionWhenTheCheckFailsOutright()
    {
        var page = Render(() => null);

        page.WaitForAssertion(() => Assert.Contains("1.16.6", page.Markup));
        Assert.Contains("1.12.0", page.Markup);
    }
}
