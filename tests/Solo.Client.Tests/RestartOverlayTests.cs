using System.Net;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Solo.Client.Components;
using Solo.Client.State;

namespace Solo.Client.Tests;

/// <summary>
/// The overlay that covers the app while the instance restarts, and the polling
/// that decides when to take it down by reloading.
/// </summary>
public class RestartOverlayTests : ComponentTestBase
{
    private readonly StubHttp _http = new();
    private readonly Queue<HttpStatusCode> _answers = new();

    public RestartOverlayTests()
    {
        // The last answer repeats, so a test only lists the ones that change.
        _http.Route("api/ready", () =>
            (_answers.Count > 1 ? _answers.Dequeue() : _answers.Peek(), null));
        Services.AddScoped(sp => new InstanceRestart(
            new HttpClient(_http) { BaseAddress = _http.Instance.BaseAddress },
            sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>())
        {
            PollInterval = TimeSpan.Zero,
        });
    }

    private InstanceRestart Restart => Services.GetRequiredService<InstanceRestart>();

    private bool Reloaded => Services.GetRequiredService<BunitNavigationManager>()
        .History.Any(h => h.Options.ForceLoad);

    private int Polls => _http.Paths.Count(p => p == "GET /api/ready");

    [Fact]
    public void Nothing_is_covered_until_a_restart_begins()
    {
        _answers.Enqueue(HttpStatusCode.OK);
        var overlay = Render<RestartOverlay>();

        Assert.Empty(overlay.FindAll(".restart-overlay"));
        Assert.Equal(0, Polls);
    }

    [Fact]
    public async Task The_app_is_covered_until_the_instance_answers_ready_and_then_reloads()
    {
        _answers.Enqueue(HttpStatusCode.ServiceUnavailable);
        _answers.Enqueue(HttpStatusCode.BadGateway);
        _answers.Enqueue(HttpStatusCode.OK);
        var overlay = Render<RestartOverlay>();

        await overlay.InvokeAsync(() => Restart.BeginAsync(null));

        overlay.WaitForAssertion(() => Assert.True(Reloaded));
        Assert.Contains("Restarting Solo instance", overlay.Markup);
        Assert.Equal(3, Polls);
    }

    /// <summary>
    /// The process that was asked to restart still answers for a moment, and
    /// an instance from before the ready endpoint answers it with a 404 — so a
    /// 404 is only a sign of the new process once the old one has been seen
    /// to go.
    /// </summary>
    [Fact]
    public async Task A_404_counts_as_back_only_after_the_instance_was_seen_down()
    {
        _answers.Enqueue(HttpStatusCode.NotFound);
        _answers.Enqueue(HttpStatusCode.NotFound);
        _answers.Enqueue(HttpStatusCode.ServiceUnavailable);
        _answers.Enqueue(HttpStatusCode.NotFound);
        var overlay = Render<RestartOverlay>();

        await overlay.InvokeAsync(() => Restart.BeginAsync(null));

        overlay.WaitForAssertion(() => Assert.True(Reloaded));
        Assert.Equal(4, Polls);
    }

    [Fact]
    public async Task A_restart_that_takes_too_long_says_why_and_offers_a_reload()
    {
        Services.AddScoped(sp => new InstanceRestart(
            new HttpClient(_http) { BaseAddress = _http.Instance.BaseAddress },
            sp.GetRequiredService<Microsoft.JSInterop.IJSRuntime>())
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            SlowAfter = TimeSpan.Zero,
        });
        _answers.Enqueue(HttpStatusCode.ServiceUnavailable);
        var overlay = Render<RestartOverlay>();

        await overlay.InvokeAsync(() => Restart.BeginAsync(null));

        overlay.WaitForAssertion(() => Assert.Contains("no restart policy", overlay.Markup));
        Assert.False(Reloaded);
        overlay.FindAll("button").Single(b => b.TextContent.Trim() == "Reload now").Click();
        Assert.True(Reloaded);
    }

    [Fact]
    public void What_the_restart_left_to_say_is_shown_once_the_app_is_back()
    {
        JSInterop.Setup<string?>("sessionStorage.getItem", "solo.restart.notice")
            .SetResult(System.Text.Json.JsonSerializer.Serialize(
                new InstanceRestart.Notice("Roadmap 1.1.0 installed.", Severity.Success)));
        Render<MudSnackbarProvider>();
        var snackbar = Services.GetRequiredService<ISnackbar>();

        Render<RestartOverlay>();

        Assert.Contains(snackbar.ShownSnackbars, s => s.Message == "Roadmap 1.1.0 installed.");
        Assert.Single(JSInterop.Invocations["sessionStorage.removeItem"]);
    }
}
