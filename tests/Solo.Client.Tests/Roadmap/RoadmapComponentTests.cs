using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Solo.Roadmap.Client;
using Solo.Roadmap.Contracts;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests.Roadmap;

/// <summary>The Roadmaps extension's client components, rendered against a stubbed API.</summary>
public class RoadmapComponentTests : ComponentTestBase
{
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RoadmapId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid BugsBoard = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private readonly StubHttp _http = new();
    private readonly FakeLiveTopics _live = new();

    public RoadmapComponentTests()
    {
        Services.AddSingleton<ISoloClientApi>(_http.CreateClient());
        Services.AddSingleton<ISoloLiveTopics>(_live);
    }

    private static RoadmapCandidateDto Candidate(string name, int number, bool bugs = false,
        TaskState status = TaskState.Unstarted, DateOnly? start = null, DateOnly? deadline = null,
        bool closedBoard = false) =>
        new(TestData.Task(number: number, status: status, start: start, deadline: deadline) with { Name = name },
            bugs ? BugsBoard : TestData.BoardId, bugs ? "Bugs" : "Features", bugs ? "BUGS" : "FEAT",
            BoardIsOpen: !closedBoard);

    [Fact]
    public void The_script_and_stylesheet_urls_carry_the_package_version()
    {
        var version = typeof(RoadmapApi).Assembly.GetName().Version!.ToString(3);
        var api = new RoadmapApi(_http.CreateClient());

        Assert.Equal($"https://solo.test/api/extensions/roadmap/content/roadmap.js?v={version}",
            api.Content("roadmap.js").ToString());
        Assert.EndsWith($"roadmap.css?v={version}", api.Content("roadmap.css").ToString());
    }

    // -- The task picker ------------------------------------------------------------

    private async Task<IRenderedComponent<MudDialogProvider>> OpenPickerAsync(params RoadmapCandidateDto[] candidates)
    {
        _http.RouteJson($"roadmaps/{RoadmapId}/candidates", candidates);
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<TaskPickerDialog>("Add a task",
            new DialogParameters<TaskPickerDialog>
            {
                { d => d.ProjectId, ProjectId },
                { d => d.ProjectAlias, "SOLO" },
                { d => d.RoadmapId, RoadmapId },
            }));
        // Loaded once the progress bar has given way to results or a caption.
        provider.WaitForAssertion(() =>
        {
            Assert.NotEmpty(provider.FindAll(".roadmap-picker__results"));
            Assert.Empty(provider.FindAll(".roadmap-picker__results .mud-progress-linear"));
        });
        return provider;
    }

    private static List<string> Boards(IRenderedComponent<MudDialogProvider> picker) =>
        picker.FindAll(".roadmap-picker__board").Select(b => b.TextContent.Trim()).ToList();

    private static List<string> Options(IRenderedComponent<MudDialogProvider> picker) =>
        picker.FindAll(".roadmap-picker__name").Select(o => o.TextContent.Trim()).ToList();

    [Fact]
    public async Task The_picker_groups_candidates_by_board_in_board_order()
    {
        var picker = await OpenPickerAsync(
            Candidate("Crash on resume", 3, bugs: true),
            Candidate("Leak in menus", 4, bugs: true),
            Candidate("Photo mode", 7),
            Candidate("Save system", 8));

        Assert.Equal(["Bugs", "Features"], Boards(picker));
        Assert.Equal(["Crash on resume", "Leak in menus", "Photo mode", "Save system"], Options(picker));
    }

    [Fact]
    public async Task The_picker_searches_names_and_serials_and_drops_empty_boards()
    {
        var picker = await OpenPickerAsync(
            Candidate("Crash on resume", 3, bugs: true),
            Candidate("Photo mode", 7),
            Candidate("Crashing save", 8));

        picker.Find("input").Input("crash");
        Assert.Equal(["Crash on resume", "Crashing save"], Options(picker));
        Assert.Equal(["Bugs", "Features"], Boards(picker));

        picker.Find("input").Input("SOLO-FEAT-7");
        Assert.Equal(["Photo mode"], Options(picker));
        Assert.Equal(["Features"], Boards(picker));

        picker.Find("input").Input("nothing like this");
        Assert.Empty(Options(picker));
        Assert.Contains("No tasks match.", picker.Markup);
    }

    [Fact]
    public async Task Each_option_shows_its_serial_status_and_dates()
    {
        var picker = await OpenPickerAsync(
            Candidate("Ranged", 5, status: TaskState.InProgress,
                start: new DateOnly(2026, 10, 3), deadline: new DateOnly(2026, 10, 10)),
            Candidate("Undated", 6));

        var ranged = picker.FindAll(".roadmap-picker__option")[0];
        Assert.Contains("SOLO-FEAT-5", ranged.TextContent);
        Assert.Contains("In progress", ranged.TextContent);
        Assert.Contains("3–10 Oct", ranged.TextContent);
        Assert.Empty(picker.FindAll(".roadmap-picker__option")[1].QuerySelectorAll(".roadmap-picker__dates"));

        // Dates sit before the status, which ends every row, so the chips line up.
        var classes = ranged.Children.Select(c => c.ClassName ?? "").ToList();
        Assert.True(classes.FindIndex(c => c.Contains("roadmap-picker__dates")) <
                    classes.FindIndex(c => c.Contains("roadmap-picker__status")));
        Assert.Contains("roadmap-picker__status", classes[^1]);
    }

    [Fact]
    public async Task Finished_tasks_are_hidden_until_switched_on()
    {
        var picker = await OpenPickerAsync(
            Candidate("Open work", 1),
            Candidate("Done already", 2, status: TaskState.Finished));

        Assert.Equal(["Open work"], Options(picker));

        picker.FindAll(".roadmap-picker__filters input")[0].Change(true);

        Assert.Equal(["Open work", "Done already"], Options(picker));
    }

    [Fact]
    public async Task A_closed_boards_tasks_are_hidden_until_switched_on_and_marked_when_shown()
    {
        var picker = await OpenPickerAsync(
            Candidate("Live bug", 3, bugs: true),
            Candidate("Shelved feature", 4, closedBoard: true));

        Assert.Equal(["Live bug"], Options(picker));
        Assert.Equal(["Bugs"], Boards(picker));

        picker.FindAll(".roadmap-picker__filters input")[1].Change(true);

        Assert.Equal(["Live bug", "Shelved feature"], Options(picker));
        Assert.Equal(["Bugs", "Features · closed"], Boards(picker));
    }

    [Fact]
    public async Task With_everything_hidden_the_picker_says_what_to_switch_on()
    {
        var picker = await OpenPickerAsync(Candidate("Done", 1, status: TaskState.Finished));

        Assert.Empty(Options(picker));
        Assert.Contains("Finished tasks and closed boards are hidden", picker.Markup);
    }

    [Fact]
    public async Task Arrow_keys_move_the_highlight()
    {
        var picker = await OpenPickerAsync(Candidate("First", 1), Candidate("Second", 2), Candidate("Third", 3));

        var input = picker.Find("input");
        input.KeyDown("ArrowDown");
        input.KeyDown("ArrowDown");
        input.KeyDown("ArrowUp");

        Assert.Equal("Second", picker.Find(".roadmap-picker__option.is-active .roadmap-picker__name").TextContent);
    }

    // -- A task's roadmaps in its task view -------------------------------------------

    [Fact]
    public void A_tasks_roadmaps_render_as_chips_linking_to_each()
    {
        var task = TestData.Task();
        _http.RouteJson($"tasks/{task.Id}/roadmaps", new[]
        {
            new TaskRoadmapDto(RoadmapId, "Launch", "SOLO"),
            new TaskRoadmapDto(Guid.Parse("99999999-9999-9999-9999-999999999999"), "Polish", "SOLO"),
        });

        var memberships = Render<RoadmapMemberships>(p => p.Add(m => m.Task, task));

        memberships.WaitForAssertion(() => Assert.Equal(2, memberships.FindAll(".mud-chip").Count));
        Assert.Contains("Roadmaps:", memberships.Markup);
        Assert.Contains($"href=\"projects/SOLO/roadmaps/{RoadmapId}\"", memberships.Markup);
    }

    [Fact]
    public void A_task_on_no_roadmap_adds_nothing_to_its_task_view()
    {
        var task = TestData.Task();
        _http.RouteJson($"tasks/{task.Id}/roadmaps", Array.Empty<TaskRoadmapDto>());

        var memberships = Render<RoadmapMemberships>(p => p.Add(m => m.Task, task));

        memberships.WaitForAssertion(() => Assert.Contains($"GET /api/ext/roadmap/tasks/{task.Id}/roadmaps", _http.Paths));
        Assert.Equal(string.Empty, memberships.Markup.Trim());
    }

    [Fact]
    public void The_chips_follow_the_drawer_to_another_task_but_not_a_save_of_the_same_one()
    {
        var first = TestData.Task();
        var second = TestData.Task();
        _http.RouteJson($"tasks/{first.Id}/roadmaps", new[] { new TaskRoadmapDto(RoadmapId, "Launch", "SOLO") });
        _http.RouteJson($"tasks/{second.Id}/roadmaps", Array.Empty<TaskRoadmapDto>());

        var memberships = Render<RoadmapMemberships>(p => p.Add(m => m.Task, first));
        memberships.WaitForAssertion(() => Assert.Single(memberships.FindAll(".mud-chip")));

        memberships.Render(p => p.Add(m => m.Task, first with { Version = 2 }));
        Assert.Single(_http.Paths, p => p.Contains($"tasks/{first.Id}/roadmaps"));

        memberships.Render(p => p.Add(m => m.Task, second));
        memberships.WaitForAssertion(() => Assert.Empty(memberships.FindAll(".mud-chip")));
    }

    // -- The sidebar section -------------------------------------------------------------

    private static ProjectSummaryDto Project(ProjectRole role) =>
        new(ProjectId, "Solo", "SOLO", null, null, role, false);

    private IRenderedComponent<RoadmapNavSection> RenderSection(ProjectRole role) =>
        Render<RoadmapNavSection>(p => p
            .Add(s => s.Project, Project(role))
            .Add(s => s.CanWrite, role == ProjectRole.ReadWrite));

    [Fact]
    public void The_section_lists_the_projects_roadmaps_as_links()
    {
        _http.RouteJson($"projects/{ProjectId}/roadmaps", new[] { new RoadmapSummaryDto(RoadmapId, "2027 Launch", 1) });

        var section = RenderSection(ProjectRole.ReadWrite);

        section.WaitForAssertion(() => Assert.Contains("2027 Launch", section.Markup));
        Assert.Contains($"href=\"projects/SOLO/roadmaps/{RoadmapId}\"", section.Markup);
        Assert.Contains("Add roadmap", section.Markup);
    }

    [Fact]
    public void An_empty_project_says_so_and_a_reader_gets_no_add_action()
    {
        _http.RouteJson($"projects/{ProjectId}/roadmaps", Array.Empty<RoadmapSummaryDto>());

        var section = RenderSection(ProjectRole.Read);

        section.WaitForAssertion(() => Assert.Contains("No roadmaps.", section.Markup));
        Assert.DoesNotContain("Add roadmap", section.Markup);
    }

    [Fact]
    public void A_failed_load_is_a_caption_not_an_exception()
    {
        _http.Route($"projects/{ProjectId}/roadmaps", HttpStatusCode.InternalServerError);

        var section = RenderSection(ProjectRole.ReadWrite);

        section.WaitForAssertion(() => Assert.Contains("Couldn't load roadmaps.", section.Markup));
    }

    [Fact]
    public void A_change_raised_for_the_project_reloads_the_list()
    {
        var titles = new[] { "Before" };
        _http.Route($"projects/{ProjectId}/roadmaps", HttpStatusCode.OK,
            () => titles.Select(t => new RoadmapSummaryDto(RoadmapId, t, 1)).ToArray());
        var section = RenderSection(ProjectRole.ReadWrite);
        section.WaitForAssertion(() => Assert.Contains("Before", section.Markup));

        titles = ["After"];
        RoadmapNavEvents.Raise(Guid.NewGuid()); // another project: ignored
        RoadmapNavEvents.Raise(ProjectId);

        section.WaitForAssertion(() => Assert.Contains("After", section.Markup));
        Assert.Equal(2, _http.Paths.Count(p => p.Contains($"projects/{ProjectId}/roadmaps")));
    }

    [Fact]
    public async Task A_change_someone_else_made_reloads_the_list_until_it_is_gone()
    {
        var titles = new[] { "Before" };
        _http.Route($"projects/{ProjectId}/roadmaps", HttpStatusCode.OK,
            () => titles.Select(t => new RoadmapSummaryDto(RoadmapId, t, 1)).ToArray());
        var section = RenderSection(ProjectRole.Read);
        section.WaitForAssertion(() => Assert.Contains("Before", section.Markup));
        Assert.Contains((ProjectId, RoadmapTopics.List), _live.Held);

        titles = ["After"];
        await _live.SendAsync(ProjectId, RoadmapTopics.List, RoadmapTopics.ListChanged);
        section.WaitForAssertion(() => Assert.Contains("After", section.Markup));

        await DisposeComponentsOnDispatcherAsync();
        Assert.Empty(_live.Held);
    }

    // -- The project home and the project card -----------------------------------------

    private static readonly Guid PolishId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private void RouteTwoRoadmaps() =>
        _http.RouteJson($"projects/{ProjectId}/roadmaps", new[]
        {
            new RoadmapSummaryDto(RoadmapId, "2027 Launch", 3),
            new RoadmapSummaryDto(PolishId, "Polish", 1),
        });

    private IRenderedComponent<RoadmapHomeSection> RenderHome(ProjectRole role) =>
        Render<RoadmapHomeSection>(p => p
            .Add(s => s.Project, Project(role))
            .Add(s => s.CanWrite, role == ProjectRole.ReadWrite));

    [Fact]
    public void The_home_section_shows_a_card_per_roadmap_and_a_click_opens_it()
    {
        RouteTwoRoadmaps();

        var home = RenderHome(ProjectRole.ReadWrite);

        home.WaitForAssertion(() => Assert.Equal(2, home.FindAll(".roadmap-home__card").Count));
        Assert.Contains("2027 Launch", home.FindAll(".roadmap-home__card")[0].TextContent);
        Assert.Equal(2, home.FindAll(".roadmap-home__card button").Count(b => b.TextContent.Contains("Rename")));
        Assert.Single(home.FindAll("[aria-label='New roadmap']"));

        home.FindAll(".roadmap-home__open")[1].Click();
        Assert.EndsWith($"projects/SOLO/roadmaps/{PolishId}", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void A_reader_gets_the_cards_without_rename_or_add()
    {
        RouteTwoRoadmaps();

        var home = RenderHome(ProjectRole.Read);

        home.WaitForAssertion(() => Assert.Equal(2, home.FindAll(".roadmap-home__card").Count));
        Assert.DoesNotContain("Rename", home.Markup);
        Assert.Empty(home.FindAll("[aria-label='New roadmap']"));
    }

    [Fact]
    public void An_empty_home_section_offers_a_writer_the_first_roadmap()
    {
        _http.RouteJson($"projects/{ProjectId}/roadmaps", Array.Empty<RoadmapSummaryDto>());

        var writer = RenderHome(ProjectRole.ReadWrite);
        writer.WaitForAssertion(() => Assert.Contains("No roadmaps yet.", writer.Markup));
        Assert.Contains("Create one", writer.Markup);

        var reader = RenderHome(ProjectRole.Read);
        reader.WaitForAssertion(() => Assert.Contains("No roadmaps yet.", reader.Markup));
        Assert.DoesNotContain("Create one", reader.Markup);
    }

    [Fact]
    public async Task Renaming_from_a_card_saves_over_the_version_the_card_read()
    {
        _http.Route($"roadmaps/{RoadmapId}", HttpStatusCode.OK,
            new RoadmapSummaryDto(RoadmapId, "2027 Launch, revised", 4), HttpMethod.Put);
        RouteTwoRoadmaps();
        var dialogs = Render<MudDialogProvider>();
        var home = RenderHome(ProjectRole.ReadWrite);
        home.WaitForAssertion(() => Assert.Equal(2, home.FindAll(".roadmap-home__card").Count));

        await home.InvokeAsync(() => home.FindAll(".roadmap-home__card button")[0].Click());
        dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindAll("input")));
        dialogs.Find("input").Input("2027 Launch, revised");
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Rename").Click();

        home.WaitForAssertion(() => Assert.Contains($"PUT /api/ext/roadmap/projects/{ProjectId}/roadmaps/{RoadmapId}", _http.Paths));
        var body = _http.Bodies[_http.Paths.IndexOf($"PUT /api/ext/roadmap/projects/{ProjectId}/roadmaps/{RoadmapId}")];
        Assert.Contains("\"title\":\"2027 Launch, revised\"", body);
        Assert.Contains("\"version\":3", body);
    }

    [Fact]
    public void The_project_card_links_each_roadmap()
    {
        RouteTwoRoadmaps();

        var card = Render<RoadmapCardSection>(p => p.Add(s => s.Project, Project(ProjectRole.Read)));

        card.WaitForAssertion(() => Assert.Equal(2, card.FindAll("a.roadmap-card-link").Count));
        Assert.Equal($"projects/SOLO/roadmaps/{RoadmapId}", card.FindAll("a.roadmap-card-link")[0].GetAttribute("href"));
        Assert.Contains("Polish", card.FindAll("a.roadmap-card-link")[1].TextContent);
    }

    [Fact]
    public void A_project_with_no_roadmaps_says_so_on_its_card()
    {
        _http.RouteJson($"projects/{ProjectId}/roadmaps", Array.Empty<RoadmapSummaryDto>());

        var card = Render<RoadmapCardSection>(p => p.Add(s => s.Project, Project(ProjectRole.Read)));

        card.WaitForAssertion(() => Assert.Contains("No roadmaps.", card.Markup));
    }
}
