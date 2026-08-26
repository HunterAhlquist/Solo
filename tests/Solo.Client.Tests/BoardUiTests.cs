using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Api;
using Solo.Client.Components;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// Renders the board surface the way the browser does. Every bug these cover was
/// found by a human clicking, not by a test — which is the point of having them now.
/// </summary>
public class BoardUiTests : BunitContext, IAsyncLifetime
{
    private readonly StubHttp _http = new();
    private readonly FakeBoardHub _hub = new();
    private readonly BoardStore _store;

    public BoardUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddMudMarkdownServices();

        var api = _http.CreateClient();
        var session = new UserSession(api);
        _store = new BoardStore(api, _hub, session);

        Services.AddSingleton(api);
        Services.AddSingleton(session);
        Services.AddSingleton(_store);
        Services.AddSingleton<IBoardHubClient>(_hub);
        Services.AddScoped<SaveGuard>();
        Services.AddScoped<RecentProjectsStore>();
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private static readonly ProjectMemberDto Hunter = TestData.Member(TestData.MeId, "Hunter Ahlquist");

    /// <summary>
    /// Queues the responses the board page fetches on entry. It must load the store
    /// itself: Board.OnParametersSetAsync short-circuits when the store already
    /// holds this board, and would then never build its render state.
    /// </summary>
    private void QueueBoard(BoardDetailDto board)
    {
        // Order matters: the page hydrates the session before it loads the board.
        _http.RespondJson(new UserDto(TestData.MeId, "me@example.com", "Me", null, true));
        _http.RespondJson(board);            // GET /api/boards/{id}
        _http.RespondJson(new[] { Hunter }); // GET /api/projects/{id}/members
    }

    private IRenderedComponent<Pages.Board> RenderBoard() =>
        Render<Pages.Board>(p => p
            .Add(b => b.ProjectAlias, "PROJ")
            .Add(b => b.BoardAlias, "BRD"));

    /// <summary>The drawer reads labels and members off a loaded store.</summary>
    private async Task LoadStoreAsync(BoardDetailDto board)
    {
        _http.RespondJson(board);
        _http.RespondJson(new[] { Hunter });
        await _store.LoadAsync(board.Id);
    }

    // -- Bug: a newly created task didn't appear until the board was refetched ----

    [Fact]
    public async Task A_task_added_to_the_store_appears_on_the_board_immediately()
    {
        QueueBoard(TestData.Board(epics: [TestData.Epic()]));
        var page = RenderBoard();

        Assert.DoesNotContain("Fix the netcode", page.Markup);

        // Exactly what Board.CreateTaskAsync does with the DTO the API returns.
        var created = TestData.Task(status: TaskState.Unstarted) with { Name = "Fix the netcode" };
        await page.InvokeAsync(() => _store.ApplyTask(created));

        Assert.Contains("Fix the netcode", page.Markup);
    }

    [Fact]
    public async Task A_task_added_into_an_epic_lane_appears_immediately()
    {
        QueueBoard(TestData.Board(epics: [TestData.Epic()]));
        var page = RenderBoard();

        var created = TestData.Task(epicId: TestData.EpicId) with { Name = "Bake lightmaps" };
        await page.InvokeAsync(() => _store.ApplyTask(created));

        Assert.Contains("Bake lightmaps", page.Markup);
    }

    [Fact]
    public async Task A_task_removed_from_the_store_disappears_immediately()
    {
        var task = TestData.Task() with { Name = "Delete me" };
        QueueBoard(TestData.Board(tasks: [task]));
        var page = RenderBoard();

        Assert.Contains("Delete me", page.Markup);

        await page.InvokeAsync(() => _store.RemoveTask(task.Id));

        Assert.DoesNotContain("Delete me", page.Markup);
    }

    // -- Epic bands reorder by dragging their gutter, not by move buttons --------

    private static readonly Guid EpicA = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid EpicB = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid EpicC = Guid.Parse("a0000000-0000-0000-0000-000000000003");

    private const string Gutter = ".kanban-lane__gutter--openable";
    private const string EpicDropSlot = ".epic-drop-slot";

    /// <summary>Three bands, ranked A → B → C, with a rerank reply waiting.</summary>
    private IRenderedComponent<Pages.Board> RenderThreeBands()
    {
        QueueBoard(TestData.Board(epics:
        [
            TestData.Epic(EpicA, 1024m),
            TestData.Epic(EpicB, 2048m),
            TestData.Epic(EpicC, 3072m),
        ]));
        var page = RenderBoard();
        _http.RespondJson(TestData.Epic(EpicC, 512m));
        return page;
    }

    private (Guid? Preceding, Guid? Following) RerankOf(Guid epicId) =>
        NeighborsSentTo($"PUT /api/epics/{epicId}/rank");

    /// <summary>The neighbours a rerank asked the server to slot the item between.</summary>
    private (Guid? Preceding, Guid? Following) NeighborsSentTo(string put)
    {
        Assert.Contains(put, _http.Paths);
        var body = System.Text.Json.JsonDocument.Parse(_http.Bodies[_http.Paths.IndexOf(put)]).RootElement;
        static Guid? Read(System.Text.Json.JsonElement e) =>
            e.ValueKind == System.Text.Json.JsonValueKind.Null ? null : e.GetGuid();
        return (Read(body.GetProperty("precedingId")), Read(body.GetProperty("followingId")));
    }

    [Fact]
    public void An_epic_band_is_a_drag_handle_and_has_no_move_buttons()
    {
        var page = RenderThreeBands();

        Assert.Empty(page.FindAll("[aria-label='Move up']"));
        Assert.Empty(page.FindAll("[aria-label='Move down']"));
        Assert.Equal(3, page.FindAll(".kanban-lane__drag-handle").Count);
        Assert.Equal(4, page.FindAll(EpicDropSlot).Count);
        Assert.All(page.FindAll(Gutter), g => Assert.Equal("true", g.GetAttribute("draggable")));
    }

    [Fact]
    public void Dropping_in_the_first_slot_ranks_the_band_before_every_other_band()
    {
        var page = RenderThreeBands();

        page.FindAll(Gutter)[2].DragStart();
        page.FindAll(EpicDropSlot)[0].Drop();

        Assert.Equal((null, EpicA), RerankOf(EpicC));
    }

    [Fact]
    public void Dropping_in_the_last_slot_ranks_the_band_after_every_other_band()
    {
        var page = RenderThreeBands();

        page.FindAll(Gutter)[0].DragStart();
        page.FindAll(EpicDropSlot)[3].Drop();

        Assert.Equal((EpicC, (Guid?)null), RerankOf(EpicA));
    }

    [Fact]
    public void Dropping_on_the_line_between_two_bands_uses_those_bands_as_neighbours()
    {
        var page = RenderThreeBands();

        page.FindAll(Gutter)[2].DragStart();
        page.FindAll(EpicDropSlot)[1].Drop();

        Assert.Equal((EpicA, (Guid?)EpicB), RerankOf(EpicC));
    }

    /// <summary>
    /// A task drag reaches the insertion slots too. They must ignore it, or a
    /// card dropped between bands would silently reorder the epics instead.
    /// </summary>
    [Fact]
    public void Dropping_in_an_epic_slot_with_no_band_being_dragged_reranks_nothing()
    {
        var page = RenderThreeBands();

        page.FindAll(EpicDropSlot)[0].Drop();

        Assert.DoesNotContain(_http.Paths, p => p.Contains("/rank", StringComparison.Ordinal));
    }

    /// <summary>The insertion point under the pointer is marked with a line.</summary>
    [Fact]
    public void The_insertion_slot_under_the_pointer_is_highlighted_while_dragging()
    {
        var page = RenderThreeBands();

        page.FindAll(Gutter)[2].DragStart();
        page.FindAll(EpicDropSlot)[1].DragEnter();

        Assert.Single(page.FindAll(".epic-drop-slot--active"));
        Assert.Single(page.FindAll(".kanban-lane--dragging"));

        page.FindAll(Gutter)[2].DragEnd();

        Assert.Empty(page.FindAll(".epic-drop-slot--active"));
        Assert.Empty(page.FindAll(".kanban-lane--dragging"));
    }

    // -- Bug: opening a drawer threw on MarkdownEditor's missing Class parameter --

    [Fact]
    public async Task Opening_a_task_drawer_renders_without_throwing()
    {
        var task = TestData.Task(assignees: [TestData.MeId]) with { Name = "Tune the boss fight" };
        await LoadStoreAsync(TestData.Board(tasks: [task]));

        var drawer = Render<DetailDrawer>(p => p
            .Add(d => d.Task, task)
            .Add(d => d.CanWrite, true));

        Assert.Contains("Tune the boss fight", drawer.Markup);
        Assert.Contains("Description", drawer.Markup);
        Assert.Contains("Sub-tasks", drawer.Markup);

        var ownership = drawer.Find(".detail-drawer__ownership");
        Assert.Contains("Assignees", ownership.TextContent);
        Assert.Contains("Deadline", ownership.TextContent);
        Assert.Single(ownership.QuerySelectorAll(".detail-drawer__assignees"));
        Assert.Single(ownership.QuerySelectorAll(".deadline-picker"));
    }

    [Fact]
    public async Task Opening_an_epic_drawer_renders_without_throwing()
    {
        var epic = TestData.Epic() with { Name = "Multiplayer" };
        await LoadStoreAsync(TestData.Board(epics: [epic]));

        var drawer = Render<DetailDrawer>(p => p
            .Add(d => d.Epic, epic)
            .Add(d => d.CanWrite, true));

        Assert.Contains("Multiplayer", drawer.Markup);
        Assert.Contains("Description", drawer.Markup);
    }

    // -- Moving a task to another board -----------------------------------------

    [Fact]
    public async Task Only_a_task_can_be_moved_to_another_board_and_only_by_a_writer()
    {
        var task = TestData.Task() with { Name = "Travelling task" };
        var epic = TestData.Epic() with { Name = "Multiplayer" };
        await LoadStoreAsync(TestData.Board(tasks: [task], epics: [epic]));

        var writer = Render<DetailDrawer>(p => p.Add(d => d.Task, task).Add(d => d.CanWrite, true));
        var reader = Render<DetailDrawer>(p => p.Add(d => d.Task, task).Add(d => d.CanWrite, false));
        // An epic is the board's own furniture — it has nowhere else to go.
        var epicDrawer = Render<DetailDrawer>(p => p.Add(d => d.Epic, epic).Add(d => d.CanWrite, true));

        Assert.Contains("Move to board", writer.Markup);
        Assert.DoesNotContain("Move to board", reader.Markup);
        Assert.DoesNotContain("Move to board", epicDrawer.Markup);
    }

    private static readonly Guid ArtBoardId = Guid.Parse("b0000000-0000-0000-0000-000000000001");

    /// <summary>
    /// The picker, hosted the way the app hosts it: MudDialog renders nothing
    /// without a dialog instance to cascade, and MudSelect teleports its items
    /// into the popover provider — so both providers share this render tree, and
    /// the returned fragment spans them.
    /// </summary>
    private async Task<IRenderedComponent<ContainerFragment>> ShowMovePickerAsync()
    {
        _http.RouteJson("/epics", new[] { TestData.Epic() with { Name = "Concept art" } });
        _http.RouteJson($"/projects/{TestData.ProjectId}/boards", new[]
        {
            new BoardDto(TestData.BoardId, TestData.ProjectId, "This board", "BRD", null, null, true, 1),
            new BoardDto(ArtBoardId, TestData.ProjectId, "Art board", "ART", null, null, true, 1),
            new BoardDto(Guid.NewGuid(), TestData.ProjectId, "Shipped board", "OLD", null, null, false, 1),
        });

        var host = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
        });

        var dialogs = Services.GetRequiredService<IDialogService>();
        await host.InvokeAsync(() => dialogs.ShowAsync<MoveTaskDialog>(
            "Move task",
            new DialogParameters<MoveTaskDialog>
            {
                { d => d.ProjectId, TestData.ProjectId },
                { d => d.CurrentBoardId, TestData.BoardId },
            }));
        return host;
    }

    /// <summary>MudSelect opens on mousedown, not click.</summary>
    private static void OpenSelect(IRenderedComponent<ContainerFragment> host, int index) =>
        host.FindAll("div.mud-input-control")[index].MouseDown();

    /// <summary>
    /// The picker lists somewhere <em>else</em> to put the task: the board it is
    /// already on is no destination, and neither is a closed one — those are
    /// read-only everywhere else in the app.
    /// </summary>
    [Fact]
    public async Task The_move_picker_offers_the_projects_other_open_boards()
    {
        var host = await ShowMovePickerAsync();

        OpenSelect(host, 0);

        var offered = host.FindAll(".mud-list-item").Select(i => i.TextContent).ToList();
        Assert.Contains(offered, b => b.Contains("Art board"));
        Assert.DoesNotContain(offered, b => b.Contains("This board"));
        Assert.DoesNotContain(offered, b => b.Contains("Shipped board"));
    }

    /// <summary>An epic only means anything on the board that owns it.</summary>
    [Fact]
    public async Task Choosing_a_board_offers_the_lanes_on_that_board()
    {
        var host = await ShowMovePickerAsync();

        OpenSelect(host, 0);
        host.FindAll(".mud-list-item").First(i => i.TextContent.Contains("Art board")).Click();
        OpenSelect(host, 1);

        Assert.Contains($"GET /api/boards/{ArtBoardId}/epics", _http.Paths);
        Assert.Contains(host.FindAll(".mud-list-item"), i => i.TextContent.Contains("Concept art"));
    }

    // -- Comment moderation mirrors the API: author, project owner or super admin --

    private IRenderedComponent<CommentThread> RenderThread(bool canModerate)
    {
        var taskId = Guid.NewGuid();
        _http.RouteJson("/comments", new[]
        {
            new CommentDto(Guid.NewGuid(), TestData.OtherId, "Someone Else", null,
                "Nerf the shotgun", DateTime.UtcNow, null, taskId, 1),
        });
        return Render<CommentThread>(p => p
            .Add(c => c.TaskId, taskId)
            .Add(c => c.CanModerate, canModerate));
    }

    [Fact]
    public void A_moderator_can_delete_someone_elses_comment()
    {
        var thread = RenderThread(canModerate: true);

        Assert.Contains("Nerf the shotgun", thread.Markup);
        Assert.NotEmpty(thread.FindAll("[aria-label='Delete comment']"));
    }

    [Fact]
    public void A_plain_member_cannot_delete_someone_elses_comment()
    {
        var thread = RenderThread(canModerate: false);

        Assert.Contains("Nerf the shotgun", thread.Markup);
        Assert.Empty(thread.FindAll("[aria-label='Delete comment']"));
    }

    /// <summary>The description must be a real editor, not a dead one-line label.</summary>
    [Fact]
    public async Task The_description_editor_opens_a_textarea_when_clicked()
    {
        var task = TestData.Task() with { Name = "T" };
        await LoadStoreAsync(TestData.Board(tasks: [task]));

        var drawer = Render<DetailDrawer>(p => p
            .Add(d => d.Task, task)
            .Add(d => d.CanWrite, true));

        // The comment composer is always in edit mode, so count the change rather
        // than assert on an empty page.
        var before = drawer.FindAll("textarea").Count;
        drawer.Find(".markdown-read").Click();

        Assert.Equal(before + 1, drawer.FindAll("textarea").Count);
    }

    // -- Bug: the assignee field displayed raw GUIDs -----------------------------

    [Fact]
    public void The_assignee_field_shows_names_not_user_ids()
    {
        var picker = Render<AssigneePicker>(p => p
            .Add(a => a.Members, [Hunter])
            .Add(a => a.AssigneeIds, [TestData.MeId])
            .Add(a => a.CanWrite, true));

        Assert.Contains("Hunter Ahlquist", picker.Markup);
        Assert.DoesNotContain(TestData.MeId.ToString(), picker.Markup);
    }

    [Fact]
    public void Several_assignees_collapse_to_a_count_not_a_list_of_guids()
    {
        var ada = TestData.Member(Guid.NewGuid(), "Ada");
        var picker = Render<AssigneePicker>(p => p
            .Add(a => a.Members, [Hunter, ada])
            .Add(a => a.AssigneeIds, [TestData.MeId, ada.UserId!.Value])
            .Add(a => a.CanWrite, true));

        Assert.Contains("2 people", picker.Markup);
        Assert.DoesNotContain(TestData.MeId.ToString(), picker.Markup);
    }

    // -- Bug: MudGrid's negative top margin overlaid the tab strip's click targets

    [Fact]
    public async Task The_labels_grid_is_wrapped_so_it_cannot_overlay_the_tab_strip()
    {
        // MudGrid sets margin-top:-24px and expects a padded parent. Measured in
        // Chrome: without the wrapper the grid covers the 24px above it and wins
        // every hit-test there; with it, overlap is zero.
        _http.RespondJson(Array.Empty<LabelDto>());

        var tab = Render<LabelsTab>(p => p
            .Add(t => t.ProjectId, TestData.ProjectId)
            .Add(t => t.CanWrite, true));

        var inset = tab.Find(".grid-inset");
        Assert.Contains("mud-grid", inset.InnerHtml);
    }

    [Fact]
    public void No_assignees_reads_as_nobody()
    {
        var picker = Render<AssigneePicker>(p => p
            .Add(a => a.Members, [Hunter])
            .Add(a => a.AssigneeIds, [])
            .Add(a => a.CanWrite, true));

        Assert.DoesNotContain(TestData.MeId.ToString(), picker.Markup);
    }

    // -- Bug: the sub-task label picker did nothing -----------------------------

    /// <summary>
    /// The sub-task label picker is a tag-icon MudMenu (built-in Icon activator, as
    /// the epic menu uses) — clicking it must actually open and list the project's
    /// sub-task labels. The earlier ActivatorContent activator rippled but never
    /// opened; this renders it beside a popover provider and asserts it opens.
    /// </summary>
    [Fact]
    public async Task Clicking_the_subtask_label_icon_opens_a_menu_of_subtask_labels()
    {
        var subLabel = TestData.Label(Guid.NewGuid(), "Design", "#e93d82") with { Kind = LabelKind.SubTask };
        var taskId = Guid.NewGuid();
        var task = TestData.Task(id: taskId, subTasks: [TestData.SubTask(false, taskId)]) with { Name = "T" };
        await LoadStoreAsync(TestData.Board(tasks: [task], labels: [subLabel]));

        // The MudMenu teleports its items into a MudPopoverProvider, so both must
        // share one render tree for the opened items to appear in the markup.
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<Components.SubTaskList>(1);
            builder.AddAttribute(2, nameof(Components.SubTaskList.TaskId), task.Id);
            builder.AddAttribute(3, nameof(Components.SubTaskList.SubTasks), task.SubTasks);
            builder.AddAttribute(4, nameof(Components.SubTaskList.CanWrite), true);
            builder.CloseComponent();
        });

        Assert.DoesNotContain("Design", cut.Markup); // closed: items are not shown yet

        cut.Find(".subtask-label-menu button").Click();

        Assert.Contains("Design", cut.Markup);   // the sub-task label is offered
        Assert.Contains("No label", cut.Markup);  // and so is clearing it
    }

    // -- Sub-task names are editable in place -------------------------------------

    private IRenderedComponent<Components.SubTaskList> RenderSubTasks(SubTaskDto subTask, bool canWrite = true) =>
        Render<Components.SubTaskList>(p => p
            .Add(l => l.TaskId, subTask.TaskId)
            .Add(l => l.SubTasks, (IReadOnlyList<SubTaskDto>)[subTask])
            .Add(l => l.CanWrite, canWrite));

    private const string SubTaskName = ".mud-typography-body2.flex-grow-1";
    private const string SubTaskEditor = "input[aria-label='Edit sub-task']";

    /// <summary>
    /// Clicking a sub-task's name swaps it for an inline editor; Enter commits
    /// the new name through PUT /api/subtasks/{id}, keeping the label.
    /// </summary>
    [Fact]
    public void Clicking_a_subtask_name_opens_an_editor_and_enter_saves_the_rename()
    {
        var subTask = TestData.SubTask(false);
        var cut = RenderSubTasks(subTask);
        _http.RespondJson(subTask with { Name = "Sharper name", Version = 2 });

        cut.Find(SubTaskName).Click();
        var input = cut.Find(SubTaskEditor);
        input.Input("Sharper name");
        input.KeyDown("Enter");

        var put = $"PUT /api/subtasks/{subTask.Id}";
        Assert.Contains(put, _http.Paths);
        Assert.Contains("Sharper name", _http.Bodies[_http.Paths.IndexOf(put)]);
    }

    [Fact]
    public void Subtask_rows_do_not_register_a_keyboard_interceptor_that_swallows_spaces()
    {
        var cut = RenderSubTasks(TestData.SubTask(false));

        cut.Find(SubTaskName).Click();

        var swallowingInterceptors = JSInterop.Invocations
            .Where(invocation => invocation.Identifier == "mudKeyInterceptor.connect")
            .Where(invocation =>
                Assert.IsType<MudBlazor.Services.KeyInterceptorOptions>(invocation.Arguments[2]).Keys
                    .Any(key => key.Key == " " && key.PreventDown == "key+none"));
        Assert.DoesNotContain(swallowingInterceptors, invocation =>
        {
            var targetId = Assert.IsType<string>(invocation.Arguments[1]);
            return cut.FindAll($"#{targetId}")
                .Any(target => target.QuerySelector(SubTaskEditor) is not null);
        });
    }

    /// <summary>Blur is the drawer-wide save event, and it applies here too.</summary>
    [Fact]
    public void Clicking_away_from_a_subtask_rename_commits_it()
    {
        var subTask = TestData.SubTask(false);
        var cut = RenderSubTasks(subTask);
        _http.RespondJson(subTask with { Name = "Renamed on blur", Version = 2 });

        cut.Find(SubTaskName).Click();
        var input = cut.Find(SubTaskEditor);
        input.Input("Renamed on blur");
        input.Blur();

        Assert.Contains($"PUT /api/subtasks/{subTask.Id}", _http.Paths);
    }

    [Fact]
    public void Escape_abandons_a_subtask_rename_without_saving()
    {
        var subTask = TestData.SubTask(false);
        var cut = RenderSubTasks(subTask);

        cut.Find(SubTaskName).Click();
        var input = cut.Find(SubTaskEditor);
        input.Input("Discarded draft");
        input.KeyDown("Escape");

        Assert.Empty(_http.Paths);                       // nothing was sent
        Assert.Contains(subTask.Name, cut.Markup);       // the original name is back
        Assert.Empty(cut.FindAll(SubTaskEditor));
    }

    [Fact]
    public void An_unchanged_subtask_name_is_not_saved()
    {
        var subTask = TestData.SubTask(false);
        var cut = RenderSubTasks(subTask);

        cut.Find(SubTaskName).Click();
        cut.Find(SubTaskEditor).KeyDown("Enter");

        Assert.Empty(_http.Paths);
    }

    [Fact]
    public void A_read_only_member_cannot_rename_a_subtask()
    {
        var cut = RenderSubTasks(TestData.SubTask(false), canWrite: false);

        cut.Find(SubTaskName).Click();

        Assert.Empty(cut.FindAll(SubTaskEditor));
    }

    // -- Sub-tasks reorder by dragging their handle -------------------------------

    private const string SubTaskRow = ".subtask-row";
    private const string SubTaskHandle = ".subtask-drag-handle";

    /// <summary>Three sub-tasks, ranked first → second → third, with a rerank reply waiting.</summary>
    private (IRenderedComponent<Components.SubTaskList> Page, List<SubTaskDto> Items) RenderChecklist(
        bool canWrite = true)
    {
        var taskId = Guid.NewGuid();
        List<SubTaskDto> items =
        [
            TestData.SubTask(false, taskId) with { Name = "First", Rank = 1024m },
            TestData.SubTask(false, taskId) with { Name = "Second", Rank = 2048m },
            TestData.SubTask(false, taskId) with { Name = "Third", Rank = 3072m },
        ];

        var page = Render<Components.SubTaskList>(p => p
            .Add(l => l.TaskId, taskId)
            .Add(l => l.SubTasks, (IReadOnlyList<SubTaskDto>)items)
            .Add(l => l.CanWrite, canWrite));
        _http.RespondJson(items[0] with { Rank = 4096m });
        return (page, items);
    }

    [Fact]
    public void Dragging_a_subtask_down_onto_another_ranks_it_after_that_row()
    {
        var (page, items) = RenderChecklist();

        page.FindAll(SubTaskHandle)[0].DragStart();
        page.FindAll(SubTaskRow)[2].Drop();

        Assert.Equal((items[2].Id, (Guid?)null),
            NeighborsSentTo($"PUT /api/subtasks/{items[0].Id}/rank"));
    }

    [Fact]
    public void Dragging_a_subtask_up_onto_another_ranks_it_before_that_row()
    {
        var (page, items) = RenderChecklist();

        page.FindAll(SubTaskHandle)[2].DragStart();
        page.FindAll(SubTaskRow)[0].Drop();

        Assert.Equal((null, items[0].Id),
            NeighborsSentTo($"PUT /api/subtasks/{items[2].Id}/rank"));
    }

    /// <summary>The row under the pointer is marked so the drop lands visibly.</summary>
    [Fact]
    public void The_subtask_row_under_the_pointer_is_highlighted_while_dragging()
    {
        var (page, _) = RenderChecklist();

        page.FindAll(SubTaskHandle)[2].DragStart();
        page.FindAll(SubTaskRow)[0].DragEnter();

        Assert.Single(page.FindAll(".subtask-row--drop-target"));
        Assert.Single(page.FindAll(".subtask-row--dragging"));

        page.FindAll(SubTaskHandle)[2].DragEnd();

        Assert.Empty(page.FindAll(".subtask-row--drop-target"));
        Assert.Empty(page.FindAll(".subtask-row--dragging"));
    }

    /// <summary>A drop with nothing being dragged must not reorder the checklist.</summary>
    [Fact]
    public void Dropping_on_a_subtask_with_none_being_dragged_reranks_nothing()
    {
        var (page, _) = RenderChecklist();

        page.FindAll(SubTaskRow)[0].Drop();

        Assert.DoesNotContain(_http.Paths, p => p.Contains("/rank", StringComparison.Ordinal));
    }

    [Fact]
    public void A_read_only_member_gets_no_subtask_drag_handles()
    {
        var (page, _) = RenderChecklist(canWrite: false);

        Assert.Empty(page.FindAll(SubTaskHandle));
    }

    /// <summary>A lone sub-task has nothing to swap with, so it offers no handle.</summary>
    [Fact]
    public void A_single_subtask_has_no_drag_handle()
    {
        var cut = RenderSubTasks(TestData.SubTask(false));

        Assert.Empty(cut.FindAll(SubTaskHandle));
    }

    // -- The phone layout swipes between columns, so each names itself -----------
    //
    // Below the md breakpoint the three status columns become a horizontal
    // carousel and the board's shared header row is hidden, which leaves each
    // column's own heading as the only thing naming it. That heading — and the
    // one strip element per band the carousel scrolls — are structure the CSS
    // depends on, so they are asserted here rather than left to a screenshot.

    [Fact]
    public void Each_band_puts_its_three_named_columns_in_one_strip()
    {
        QueueBoard(TestData.Board(
            epics: [TestData.Epic(EpicA, 1024m)],
            tasks:
            [
                TestData.Task(epicId: EpicA),
                TestData.Task(epicId: EpicA, status: TaskState.InProgress, rank: 2048m),
                TestData.Task(epicId: EpicA, status: TaskState.InProgress, rank: 3072m),
            ]));
        var page = RenderBoard();

        // One strip for the epic band, one for the ad-hoc band below it.
        var strips = page.FindAll(".kanban-lane__zones");
        Assert.Equal(2, strips.Count);
        Assert.All(strips, strip => Assert.Equal(3, strip.QuerySelectorAll(".kanban-column").Length));

        Assert.Equal(
            ["Unstarted 1", "In Progress 2", "Finished 0"],
            Headings(strips[0]));
    }

    /// <summary>
    /// The heading counts what is in the column, not what is on screen: past the
    /// Finished limit the overflow folds away behind an expander, and a count
    /// that shrank to 25 would say the rest had been deleted.
    /// </summary>
    [Fact]
    public void A_finished_columns_heading_counts_the_cards_folded_away_too()
    {
        var finished = Enumerable.Range(0, BoardZones.FinishedVisibleLimit + 3)
            .Select(i => TestData.Task(status: TaskState.Finished, rank: 1024m + i))
            .ToList();
        QueueBoard(TestData.Board(tasks: finished));
        var page = RenderBoard();

        Assert.Equal(
            $"Finished {BoardZones.FinishedVisibleLimit + 3}",
            Headings(page.Find(".kanban-lane__zones"))[2]);
    }

    /// <summary>Each column's heading, as "&lt;status&gt; &lt;count&gt;".</summary>
    private static List<string> Headings(AngleSharp.Dom.IElement strip) =>
        strip.QuerySelectorAll(".kanban-column__header")
            .Select(h => string.Join(' ', h.Children.Select(c => c.TextContent.Trim())))
            .ToList();

    /// <summary>
    /// Holding a dragged card against either edge steps the carousel to the next
    /// column. The behaviour is in js/solo-interop.js, where a test can't reach
    /// it — what is worth protecting here is that the two elements the script
    /// lights up still exist, since it fails silently if they don't.
    /// </summary>
    [Fact]
    public void The_board_paints_an_edge_the_drag_gesture_can_light_up_on_each_side()
    {
        QueueBoard(TestData.Board(tasks: [TestData.Task()]));
        var page = RenderBoard();

        Assert.Single(page.FindAll(".board-edge--left"));
        Assert.Single(page.FindAll(".board-edge--right"));
    }

    // -- Bug: the top-right profile menu (ActivatorContent) stopped opening -------

    /// <summary>
    /// MainLayout's profile menu uses MudMenu's built-in Icon activator with the SSO
    /// picture as an SVG image icon. Guards that this form opens on click (the
    /// ActivatorContent form did not, which is what broke the logout menu).
    /// </summary>
    [Fact]
    public void A_menu_with_an_image_icon_activator_opens_on_click()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudMenu>(1);
            builder.AddAttribute(2, nameof(MudMenu.Icon),
                "<image href=\"avatar\" width=\"24\" height=\"24\" />");
            builder.AddAttribute(3, nameof(MudMenu.ChildContent), (RenderFragment)(b =>
            {
                b.OpenComponent<MudMenuItem>(0);
                b.AddAttribute(1, nameof(MudMenuItem.ChildContent),
                    (RenderFragment)(ib => ib.AddContent(0, "Sign out")));
                b.CloseComponent();
            }));
            builder.CloseComponent();
        });

        Assert.DoesNotContain("Sign out", cut.Markup);
        cut.Find("button").Click();
        Assert.Contains("Sign out", cut.Markup);
    }
}
