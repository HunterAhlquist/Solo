using Bunit;
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
            .Add(b => b.ProjectId, TestData.ProjectId)
            .Add(b => b.BoardId, TestData.BoardId));

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
