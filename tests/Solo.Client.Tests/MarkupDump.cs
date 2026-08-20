using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Api;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// Writes the real rendered DOM to disk so a browser can measure its geometry.
/// bUnit renders markup but computes no layout, and hand-written stand-ins for
/// MudBlazor's markup are exactly how a layout bug gets "fixed" without being fixed.
///
/// Enabled only when SOLO_DUMP_MARKUP points at a directory, so it stays inert in CI.
/// </summary>
public class MarkupDump : BunitContext, IAsyncLifetime
{
    private static readonly string? OutputDirectory = Environment.GetEnvironmentVariable("SOLO_DUMP_MARKUP");

    private readonly StubHttp _http = new();

    public MarkupDump()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddMudMarkdownServices();

        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new UserSession(api));
        Services.AddSingleton(new ProjectNavStore(api));
        Services.AddScoped<RecentProjectsStore>();
        Services.AddScoped<SaveGuard>();
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    [Fact]
    public void Dump_project_home_with_the_labels_tab_active()
    {
        if (string.IsNullOrEmpty(OutputDirectory))
        {
            return; // Inert unless a dump directory is requested.
        }

        var member = TestData.Member(TestData.MeId, "Hunter Ahlquist") with { IsOwner = true };
        _http.RouteJson("/labels", new[]
        {
            TestData.Label(Guid.NewGuid(), "Bug", "#f56565"),
            TestData.Label(Guid.NewGuid(), "Art", "#4fd1c5"),
        });
        _http.RouteJson("/members", new[] { member });
        _http.RouteJson("/boards", Array.Empty<BoardDto>());
        _http.RouteJson("/api/projects/by-alias/SOLO",
            new ProjectDto(TestData.ProjectId, "Solo", "SOLO", "Games", null, 1, [member]));

        var page = Render<Pages.ProjectHome>(p => p.Add(x => x.ProjectAlias, "SOLO"));

        // Activate the label defaults tab, exactly as a click does.
        var labelsTab = page.FindAll(".mud-tab").First(t => t.TextContent.Contains("Label defaults"));
        labelsTab.Click();

        Write("project-home-labels.html", page.Markup);
    }

    private static void Write(string name, string bodyMarkup)
    {
        var path = Path.Combine(OutputDirectory!, name);
        var html =
            "<!doctype html>\n<html><head><meta charset=\"utf-8\">\n" +
            "<link rel=\"stylesheet\" href=\"mud.css\">\n" +
            "<link rel=\"stylesheet\" href=\"app.css\">\n" +
            "<style>body{margin:0;background:#15151f;color:#e5e5ec}</style>\n" +
            "</head><body class=\"mud-theme-dark\">\n" +
            "<div class=\"mud-main-content\" style=\"padding:16px\">" + bodyMarkup + "</div>\n" +
            "</body></html>";
        File.WriteAllText(path, html);
        Console.WriteLine($"### wrote {path}");
    }
}
