using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Solo.Client.Components;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// The host's task picker, opened through <see cref="ISoloTaskPicker"/> the way an
/// extension page opens it, against a stubbed API.
/// </summary>
public class TaskPickerTests : ComponentTestBase
{
    private static readonly Guid ProjectId = TestData.ProjectId;
    private static readonly Guid FeaturesId = TestData.BoardId;
    private static readonly Guid BugsId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private readonly StubHttp _http = new();
    private readonly CancellationTokenSource _cts = new();
    private Task<TaskDto?> _picked = Task.FromResult<TaskDto?>(null);

    public TaskPickerTests()
    {
        Services.AddSingleton(_http.CreateClient());
        Services.AddScoped<ISoloTaskPicker, TaskPickerService>();
    }

    private static TaskDto Item(string name, int number, Guid? epic = null, decimal rank = 1024m,
        TaskState status = TaskState.Unstarted, DateOnly? start = null, DateOnly? deadline = null) =>
        TestData.Task(number: number, epicId: epic, rank: rank, status: status, start: start, deadline: deadline)
            with { Name = name };

    private static EpicDto Epic(string name, decimal rank, Guid? group = null) =>
        new(Guid.NewGuid(), FeaturesId, name, null, rank, 1, [], group);

    private static BoardDetailDto Features(IReadOnlyList<TaskDto> tasks, IReadOnlyList<EpicDto>? epics = null,
        IReadOnlyList<EpicGroupDto>? groups = null) =>
        new(FeaturesId, ProjectId, "SOLO", "Features", "FEAT", null, null, true, 1, epics ?? [], tasks, [])
        {
            Groups = groups ?? [],
        };

    private static BoardDetailDto Bugs(IReadOnlyList<TaskDto> tasks, bool open = true) =>
        new(BugsId, ProjectId, "SOLO", "Bugs", "BUGS", null, null, open, 1, [],
            tasks.Select(t => t with { BoardId = BugsId }).ToList(), []);

    /// <summary>Opens the picker over the boards, in the order the board list gives them.</summary>
    private async Task<IRenderedComponent<MudDialogProvider>> OpenAsync(
        TaskPickerOptions? options, params BoardDetailDto[] boards)
    {
        _http.RouteJson($"projects/{ProjectId}/boards",
            boards.Select(b => new BoardDto(b.Id, ProjectId, b.Name, b.Alias, null, null, b.IsOpen, 1)).ToList());
        foreach (var board in boards)
        {
            _http.RouteJson($"api/boards/{board.Id}", board);
        }

        var provider = Render<MudDialogProvider>();
        var picker = Services.GetRequiredService<ISoloTaskPicker>();
        await provider.InvokeAsync(() => { _picked = picker.PickAsync(ProjectId, options, _cts.Token); });
        // Loaded once the progress bar has given way to results or a caption.
        provider.WaitForAssertion(() =>
        {
            Assert.NotEmpty(provider.FindAll(".task-picker__results"));
            Assert.Empty(provider.FindAll(".task-picker__results .mud-progress-linear"));
        });
        return provider;
    }

    private Task<IRenderedComponent<MudDialogProvider>> OpenAsync(params BoardDetailDto[] boards) =>
        OpenAsync(null, boards);

    private static List<string> Boards(IRenderedComponent<MudDialogProvider> picker) =>
        picker.FindAll(".task-picker__board").Select(b => b.TextContent.Trim()).ToList();

    private static List<string> Epics(IRenderedComponent<MudDialogProvider> picker) =>
        picker.FindAll(".task-picker__epic").Select(e => e.TextContent.Trim()).ToList();

    private static List<string> Options(IRenderedComponent<MudDialogProvider> picker) =>
        picker.FindAll(".task-picker__name").Select(o => o.TextContent.Trim()).ToList();

    private async Task<TaskDto?> PickedAsync() => await _picked.WaitAsync(TimeSpan.FromSeconds(5));

    // -- Grouping ----------------------------------------------------------------------

    [Fact]
    public async Task Tasks_are_grouped_by_board_in_the_order_the_board_list_gives()
    {
        var picker = await OpenAsync(
            Bugs([Item("Crash on resume", 3), Item("Leak in menus", 4)]),
            Features([Item("Photo mode", 7), Item("Save system", 8)]));

        Assert.Equal(["Bugs", "Features"], Boards(picker));
        Assert.Equal(["Crash on resume", "Leak in menus", "Photo mode", "Save system"], Options(picker));
    }

    [Fact]
    public async Task Epics_follow_the_boards_order_with_groups_and_no_epic_last()
    {
        // Groups and ungrouped epics share one rank order; a group's epics follow
        // their own ranks inside it.
        var group = new EpicGroupDto(Guid.NewGuid(), FeaturesId, "Gameplay", 2000m, 1);
        var audio = Epic("Audio", 1000m);
        var combat = Epic("Combat", 2m, group.Id);
        var movement = Epic("Movement", 1m, group.Id);
        var ui = Epic("UI", 3000m);
        var empty = Epic("Nothing in it", 500m);

        var picker = await OpenAsync(Features(
            [
                Item("Riposte", 1, combat.Id, rank: 2048m),
                Item("Loose end", 2),
                Item("Menus", 3, ui.Id),
                Item("Parry", 4, combat.Id, rank: 1024m),
                Item("Dash", 5, movement.Id),
                Item("Footsteps", 6, audio.Id),
            ],
            [audio, combat, movement, ui, empty],
            [group]));

        Assert.Equal(["Audio", "Movement", "Combat", "UI", "No epic"], Epics(picker));
        Assert.Equal(["Footsteps", "Dash", "Parry", "Riposte", "Menus", "Loose end"], Options(picker));
    }

    [Fact]
    public async Task A_board_with_no_epics_gets_no_epic_headings()
    {
        var combat = Epic("Combat", 1m);
        var picker = await OpenAsync(
            Bugs([Item("Crash", 1)]),
            Features([Item("Parry", 2, combat.Id)], [combat]));

        Assert.Equal(["Combat"], Epics(picker));
        // Only a headed lane is outlined; the epic-less board's tasks sit bare.
        var lane = Assert.Single(picker.FindAll(".task-picker__lane"));
        Assert.Contains("Parry", lane.TextContent);
    }

    [Fact]
    public void A_task_naming_an_epic_the_board_does_not_list_counts_as_having_none()
    {
        var board = Features([Item("Orphan", 1, Guid.NewGuid())], [Epic("Combat", 1m)]);

        var results = TaskPickerSearch.Filter([board], new HashSet<Guid>(), null, TaskPickerFilter.Default);

        var lane = Assert.Single(Assert.Single(results).Epics);
        Assert.Null(lane.Epic);
        Assert.Equal("Orphan", Assert.Single(lane.Tasks).Name);
    }

    // -- Searching and filtering -------------------------------------------------------

    [Fact]
    public async Task Reference_pickers_can_initially_offer_completed_tasks_and_closed_boards()
    {
        var picker = await OpenAsync(new TaskPickerOptions { IncludeFinished=true, IncludeClosedBoards=true },
            Features([Item("Completed design", 1, status:TaskState.Finished)]),
            Bugs([Item("Archived decision", 2)], open:false));
        Assert.Equal(["Completed design", "Archived decision"], Options(picker));
        picker.FindAll("input[type=checkbox]")[0].Change(false);
        Assert.Equal(["Archived decision"], Options(picker));
    }

    [Fact]
    public async Task Search_matches_names_serials_and_epics_and_drops_what_empties()
    {
        var combat = Epic("Combat", 1m);
        var picker = await OpenAsync(
            Bugs([Item("Crash on resume", 3)]),
            Features([Item("Photo mode", 7), Item("Crashing save", 8), Item("Parry", 9, combat.Id)], [combat]));

        picker.Find("input").Input("crash");
        Assert.Equal(["Crash on resume", "Crashing save"], Options(picker));
        Assert.Equal(["Bugs", "Features"], Boards(picker));
        Assert.Equal(["No epic"], Epics(picker));

        picker.Find("input").Input("SOLO-FEAT-7");
        Assert.Equal(["Photo mode"], Options(picker));
        Assert.Equal(["Features"], Boards(picker));

        picker.Find("input").Input("combat");
        Assert.Equal(["Parry"], Options(picker));
        Assert.Equal(["Combat"], Epics(picker));

        picker.Find("input").Input("nothing like this");
        Assert.Empty(Options(picker));
        Assert.Contains("No tasks match.", picker.Markup);
    }

    [Fact]
    public async Task Finished_tasks_are_hidden_until_switched_on()
    {
        var picker = await OpenAsync(Features([Item("Open work", 1), Item("Done already", 2, status: TaskState.Finished)]));

        Assert.Equal(["Open work"], Options(picker));

        picker.FindAll(".task-picker__filters input")[0].Change(true);

        Assert.Equal(["Open work", "Done already"], Options(picker));
    }

    [Fact]
    public async Task A_closed_boards_tasks_are_hidden_until_switched_on_and_marked_when_shown()
    {
        var picker = await OpenAsync(Bugs([Item("Shelved bug", 3)], open: false), Features([Item("Live feature", 4)]));

        Assert.Equal(["Live feature"], Options(picker));
        Assert.Equal(["Features"], Boards(picker));

        picker.FindAll(".task-picker__filters input")[1].Change(true);

        Assert.Equal(["Shelved bug", "Live feature"], Options(picker));
        Assert.Equal(["Bugs · closed", "Features"], Boards(picker));
    }

    [Fact]
    public async Task With_everything_hidden_the_picker_says_what_to_switch_on()
    {
        var picker = await OpenAsync(Features([Item("Done", 1, status: TaskState.Finished)]));

        Assert.Empty(Options(picker));
        Assert.Contains("Finished tasks and closed boards are hidden", picker.Markup);
    }

    [Fact]
    public async Task Excluded_tasks_are_never_offered()
    {
        var planned = Item("Planned", 1);
        var picker = await OpenAsync(
            new TaskPickerOptions { Exclude = new HashSet<Guid> { planned.Id } },
            Features([planned, Item("Unplanned", 2)]));

        Assert.Equal(["Unplanned"], Options(picker));
    }

    [Fact]
    public async Task With_every_task_excluded_the_caller_words_the_empty_list()
    {
        var planned = Item("Planned", 1);
        var picker = await OpenAsync(
            new TaskPickerOptions
            {
                Exclude = new HashSet<Guid> { planned.Id },
                AllExcludedText = "Every task is already on this roadmap.",
            },
            Features([planned]));

        Assert.Contains("Every task is already on this roadmap.", picker.Markup);
    }

    [Fact]
    public async Task A_project_without_tasks_says_so()
    {
        var picker = await OpenAsync(Features([]));

        Assert.Contains("This project has no tasks yet.", picker.Markup);
    }

    [Fact]
    public async Task A_failed_load_says_so()
    {
        _http.Route($"projects/{ProjectId}/boards", HttpStatusCode.InternalServerError);

        var picker = await OpenAsync();

        Assert.Contains("Couldn't load this project's tasks.", picker.Markup);
    }

    // -- Each option -------------------------------------------------------------------

    [Fact]
    public async Task Each_option_shows_its_serial_status_and_dates()
    {
        var picker = await OpenAsync(Features(
        [
            Item("Ranged", 5, status: TaskState.InProgress,
                start: new DateOnly(2026, 10, 3), deadline: new DateOnly(2026, 10, 10)),
            Item("Undated", 6, rank: 2048m),
        ]));

        var ranged = picker.FindAll(".task-picker__option")[0];
        Assert.Contains("SOLO-FEAT-5", ranged.TextContent);
        Assert.Contains("In progress", ranged.TextContent);
        Assert.Contains("3–10 Oct", ranged.TextContent);
        Assert.Empty(picker.FindAll(".task-picker__option")[1].QuerySelectorAll(".task-picker__dates"));

        // Dates sit before the status, which ends every row, so the chips line up.
        var classes = ranged.Children.Select(c => c.ClassName ?? "").ToList();
        Assert.True(classes.FindIndex(c => c.Contains("task-picker__dates")) <
                    classes.FindIndex(c => c.Contains("task-picker__status")));
        Assert.Contains("task-picker__status", classes[^1]);
    }

    // -- Choosing ----------------------------------------------------------------------

    [Fact]
    public async Task Arrow_keys_move_the_highlight_across_epics_and_Enter_picks_it()
    {
        var combat = Epic("Combat", 1m);
        var second = Item("Second", 2);
        var picker = await OpenAsync(Features([Item("First", 1, combat.Id), second, Item("Third", 3, rank: 2048m)], [combat]));

        var input = picker.Find("input");
        input.KeyDown("ArrowDown");
        input.KeyDown("ArrowDown");
        input.KeyDown("ArrowUp");
        Assert.Equal("Second", picker.Find(".task-picker__option.is-active .task-picker__name").TextContent);

        input.KeyDown("Enter");

        Assert.Equal(second.Id, (await PickedAsync())?.Id);
        picker.WaitForAssertion(() => Assert.Empty(picker.FindAll(".task-picker__results")));
    }

    [Fact]
    public async Task Clicking_an_option_picks_it()
    {
        var photo = Item("Photo mode", 7);
        var picker = await OpenAsync(Features([Item("Save system", 6), photo with { Rank = 2048m }]));

        picker.FindAll(".task-picker__option")[1].Click();

        Assert.Equal(photo.Id, (await PickedAsync())?.Id);
    }

    [Fact]
    public async Task Cancel_returns_nothing()
    {
        var picker = await OpenAsync(Features([Item("Save system", 1)]));

        picker.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        Assert.Null(await PickedAsync());
    }

    [Fact]
    public async Task The_callers_token_closes_the_dialog_and_returns_nothing()
    {
        var picker = await OpenAsync(Features([Item("Save system", 1)]));

        await picker.InvokeAsync(_cts.Cancel);

        Assert.Null(await PickedAsync());
        picker.WaitForAssertion(() => Assert.Empty(picker.FindAll(".task-picker__results")));
    }

    [Fact]
    public async Task The_dialog_carries_the_callers_title()
    {
        var picker = await OpenAsync(new TaskPickerOptions { Title = "Add a task on 3 Oct" }, Features([]));

        Assert.Contains("Add a task on 3 Oct", picker.Markup);
    }
}
