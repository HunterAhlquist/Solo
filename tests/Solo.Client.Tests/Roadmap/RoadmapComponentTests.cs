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

    public RoadmapComponentTests() => Services.AddSingleton<ISoloClientApi>(_http.CreateClient());

    private static RoadmapCandidateDto Candidate(string name, int number, bool bugs = false,
        TaskState status = TaskState.Unstarted, DateOnly? start = null, DateOnly? deadline = null) =>
        new(TestData.Task(number: number, status: status, start: start, deadline: deadline) with { Name = name },
            bugs ? BugsBoard : TestData.BoardId, bugs ? "Bugs" : "Features", bugs ? "BUGS" : "FEAT");

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
        provider.WaitForAssertion(() => Assert.NotEmpty(provider.FindAll(".roadmap-picker__option")));
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
}
