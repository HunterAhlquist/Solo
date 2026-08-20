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
    private readonly FakeBoardHub _hub = new();
    private readonly BoardStore _store;

    public MarkupDump()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddMudMarkdownServices();

        var api = _http.CreateClient();
        var session = new UserSession(api);
        _store = new BoardStore(api, _hub, session);

        Services.AddSingleton(api);
        Services.AddSingleton(session);
        Services.AddSingleton(new ProjectNavStore(api));
        Services.AddSingleton(_store);
        Services.AddSingleton<Solo.Client.Realtime.IBoardHubClient>(_hub);
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

    /// <summary>
    /// The board, with enough on it to see the layout do its work: two epics
    /// and the ad-hoc band, cards in all three columns, labels, a deadline
    /// countdown and a sub-task rollup. Under the md breakpoint the columns
    /// become a swipeable carousel, which is a thing only a browser can show.
    /// </summary>
    [Fact]
    public void Dump_a_board_with_epics_tasks_and_labels()
    {
        if (string.IsNullOrEmpty(OutputDirectory))
        {
            return; // Inert unless a dump directory is requested.
        }

        var board = SampleBoard();
        var member = TestData.Member(TestData.MeId, "Hunter Ahlquist") with { IsOwner = true };
        _http.RespondJson(new UserDto(TestData.MeId, "me@example.com", "Me", null, true));
        _http.RespondJson(board);
        _http.RespondJson(new[] { member });

        var page = Render<Pages.Board>(p => p
            .Add(b => b.ProjectAlias, "PXW26")
            .Add(b => b.BoardAlias, "TW"));

        Write("board.html", page.Markup);
    }

    /// <summary>
    /// The task editor. It is a 34rem drawer on a desktop and the whole screen
    /// on a phone, and only a browser can be asked which one it got.
    /// </summary>
    [Fact]
    public async Task Dump_the_task_editor()
    {
        if (string.IsNullOrEmpty(OutputDirectory))
        {
            return; // Inert unless a dump directory is requested.
        }

        var board = SampleBoard();
        _http.RespondJson(board);
        _http.RespondJson(new[] { TestData.Member(TestData.MeId, "Hunter Ahlquist") });
        await _store.LoadAsync(board.Id);

        var drawer = Render<Components.DetailDrawer>(p => p
            .Add(d => d.Task, board.Tasks[0])
            .Add(d => d.CanWrite, true));

        Write("task-editor.html", drawer.Markup);
    }

    private static BoardDetailDto SampleBoard()
    {
        var planning = Guid.Parse("a0000000-0000-0000-0000-000000000001");
        var structure = Guid.Parse("a0000000-0000-0000-0000-000000000002");
        var booth = TestData.Label(Guid.NewGuid(), "Booth", "#e53e3e");
        var tower = TestData.Label(Guid.NewGuid(), "Tower", "#38a169");

        return TestData.Board(
            epics:
            [
                TestData.Epic(planning, 1024m) with { Name = "Planning" },
                TestData.Epic(structure, 2048m) with { Name = "PVC Pipe Structure" },
            ],
            labels: [booth, tower],
            tasks:
            [
                TestData.Task(epicId: planning, status: TaskState.Unstarted, number: 11,
                        labelId: booth.Id, deadline: DateOnly.FromDateTime(DateTime.Today.AddDays(9)))
                    with { Name = "Ensure Clocktower is Acceptable" },
                TestData.Task(epicId: planning, status: TaskState.InProgress, rank: 2048m, number: 12,
                        labelId: booth.Id, subTasks: [TestData.SubTask(true), TestData.SubTask(false)])
                    with { Name = "Reserve return trip u-haul" },
                TestData.Task(epicId: planning, status: TaskState.Finished, rank: 3072m, number: 13)
                    with { Name = "Hotel Planning" },
                TestData.Task(epicId: structure, status: TaskState.InProgress, rank: 4096m, number: 14,
                        labelId: tower.Id, assignees: [TestData.MeId],
                        subTasks: [TestData.SubTask(true), TestData.SubTask(true), TestData.SubTask(false)])
                    with { Name = "Printed Tower Panels" },
                TestData.Task(epicId: structure, status: TaskState.Finished, rank: 5120m, number: 15,
                        labelId: tower.Id) with { Name = "Supplies" },
                TestData.Task(status: TaskState.Unstarted, rank: 6144m, number: 16)
                    with { Name = "Buy power amp upgrade" },
            ]);
    }

    private static void Write(string name, string bodyMarkup)
    {
        var path = Path.Combine(OutputDirectory!, name);
        var html =
            "<!doctype html>\n<html><head><meta charset=\"utf-8\">\n" +
            // As index.html: without it a phone lays the page out at 980px and
            // the responsive rules under test never match.
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n" +
            "<link rel=\"stylesheet\" href=\"mud.css\">\n" +
            "<link rel=\"stylesheet\" href=\"app.css\">\n" +
            "<style>body{margin:0;background:#15151f;color:#e5e5ec}\n" +
            // MudDrawer only takes .mud-drawer-fixed (and with it position:fixed)
            // from a surrounding MudLayout, which no dump has. Left absolute, a
            // closed right-hand drawer parks a whole drawer-width past the right
            // edge and makes the page look horizontally scrollable when it isn't.
            // (Beating MudBlazor's own .mud-drawer:not(.mud-drawer-fixed) rule.)
            ".mud-drawer{position:fixed !important}</style>\n" +
            "</head><body class=\"mud-theme-dark\">\n" +
            "<div class=\"mud-main-content\" style=\"padding:16px\">" + bodyMarkup + "</div>\n" +
            "</body></html>";
        File.WriteAllText(path, html);
        Console.WriteLine($"### wrote {path}");
    }
}
