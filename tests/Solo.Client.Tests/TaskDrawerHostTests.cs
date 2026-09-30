using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Solo.Client.Components;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests;

/// <summary>
/// The board's task drawer, opened through <see cref="ISoloTaskDrawer"/> from a
/// page that isn't a board — the way an extension page opens it.
/// </summary>
public class TaskDrawerHostTests : ComponentTestBase
{
    private static readonly Guid OtherBoardId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly StubHttp _http = new();
    private readonly FakeBoardHub _hub = new();
    private readonly BoardStore _store;
    private readonly List<TaskDrawerChange> _changes = [];

    private TaskDto _task = TestData.Task() with { Name = "Tune the boss fight" };

    /// <summary>What <c>GET api/tasks/{id}</c> answers once the drawer is open.</summary>
    private Func<(HttpStatusCode, object?)>? _taskAfterOpen;

    public TaskDrawerHostTests()
    {
        JSInterop.SetupModule("./js/finished-column.js");
        var api = _http.CreateClient();
        var session = new UserSession(api);
        _store = new BoardStore(api, _hub, session);
        Services.AddSingleton(api);
        Services.AddSingleton(session);
        Services.AddSingleton(_store);
        Services.AddSingleton<IBoardHubClient>(_hub);
        Services.AddScoped<RecentProjectsStore>();
        Services.AddScoped<DragBridge>();
        Services.AddScoped<TaskDrawerService>();
        Services.AddScoped<ISoloTaskDrawer>(sp => sp.GetRequiredService<TaskDrawerService>());
    }

    private ISoloTaskDrawer Drawer => Services.GetRequiredService<ISoloTaskDrawer>();

    private void RouteServer()
    {
        var opened = false;
        // Ahead of the task routes, which would otherwise swallow it by prefix.
        _http.RouteJson($"api/tasks/{_task.Id}/comments", Array.Empty<CommentDto>());
        _http.Route($"api/tasks/{_task.Id}/schedule", HttpStatusCode.OK,
            () => _task = _task with { DeadlineAt = new DateOnly(2026, 10, 20), Version = _task.Version + 1 },
            HttpMethod.Put);
        _http.Route($"api/tasks/{_task.Id}", HttpStatusCode.OK,
            () => _task = _task with { Name = "Renamed", Version = _task.Version + 1 }, HttpMethod.Put);
        _http.Route($"api/tasks/{_task.Id}", () =>
        {
            if (opened && _taskAfterOpen is not null)
            {
                return _taskAfterOpen();
            }

            opened = true;
            return (HttpStatusCode.OK, _task);
        }, HttpMethod.Get);
        _http.RouteJson("api/users/me", new UserDto(TestData.MeId, "me@example.com", "Me", null, false));
        _http.RouteJson($"api/projects/{TestData.ProjectId}/members", new[] { TestData.Member(TestData.MeId) });
        _http.RouteJson("api/projects/by-alias/PROJ/boards/OTHER",
            TestData.Board() with { Id = OtherBoardId, Name = "Other board", Alias = "OTHER" });
        _http.Route("api/projects/by-alias/PROJ/boards/BRD", HttpStatusCode.OK,
            () => TestData.Board(tasks: [_task]));
        _http.Route($"api/boards/{TestData.BoardId}", HttpStatusCode.OK, () => TestData.Board(tasks: [_task]));
    }

    private async Task<IRenderedComponent<TaskDrawerHost>> OpenAsync()
    {
        RouteServer();
        var host = Render<TaskDrawerHost>();
        Drawer.Changed += _changes.Add;
        await host.InvokeAsync(() => Drawer.OpenAsync(_task.Id));
        host.WaitForAssertion(() => Assert.Contains("PROJ-BRD-1", host.Markup));
        return host;
    }

    private static IRenderedComponent<MudDatePicker> Picker(IRenderedComponent<TaskDrawerHost> host, string label) =>
        host.FindComponents<MudDatePicker>().Single(p => p.Instance.Label == label);

    [Fact]
    public async Task Opening_a_task_off_a_board_page_loads_its_board_and_shows_the_boards_drawer()
    {
        var host = await OpenAsync();

        Assert.Equal(TestData.BoardId, _store.BoardId);
        Assert.Contains(TestData.BoardId, _hub.Joined);
        Assert.Contains((EntityTypes.Task, _task.Id, PresenceMode.Viewing), _hub.PresenceReports);
        Assert.Contains("Tune the boss fight", host.Markup);
        // The board page's rules: a read/write member edits.
        Assert.False(Picker(host, "Deadline").Instance.Disabled);
    }

    [Fact]
    public async Task Edits_in_the_drawer_raise_Changed_with_the_fresh_task_and_version()
    {
        var host = await OpenAsync();

        host.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "Name")
            .Find("input").Change("Renamed");
        host.WaitForAssertion(() => Assert.Contains(_changes,
            c => c is { Kind: TaskDrawerChangeKind.Saved, Task: { Name: "Renamed", Version: 2 } }));

        await host.InvokeAsync(() => Picker(host, "Deadline").Instance.DateChanged.InvokeAsync(new DateTime(2026, 10, 20)));
        host.WaitForAssertion(() => Assert.Contains(_changes,
            c => c.Task is { Version: 3 } saved && saved.DeadlineAt == new DateOnly(2026, 10, 20)));

        // The second save carried the version the first one returned.
        var schedule = _http.Bodies[_http.Paths.IndexOf($"PUT /api/tasks/{_task.Id}/schedule")];
        Assert.Contains("\"version\":2", schedule);
    }

    [Fact]
    public async Task Closing_raises_Closed_and_hands_back_the_board_it_borrowed()
    {
        var host = await OpenAsync();
        var closed = false;
        Drawer.Closed += () => closed = true;

        host.Find("button[aria-label='Close']").Click();

        host.WaitForAssertion(() => Assert.True(closed));
        Assert.DoesNotContain("PROJ-BRD-1", host.Markup);
        Assert.Null(_store.Board);
        Assert.Contains(TestData.BoardId, _hub.Left);
    }

    [Fact]
    public async Task A_task_deleted_by_someone_else_is_reported_and_the_drawer_closes()
    {
        _taskAfterOpen = () => (HttpStatusCode.NotFound, null);
        var host = await OpenAsync();

        await host.InvokeAsync(() => _hub.RaiseEntityChanged(
            new EntityChangedEvent(EntityTypes.Task, _task.Id, ChangeKind.Deleted, [], TestData.OtherId)));

        host.WaitForAssertion(() => Assert.Contains(_changes,
            c => c is { Kind: TaskDrawerChangeKind.Deleted, Task: null } && c.TaskId == _task.Id));
        host.WaitForAssertion(() => Assert.DoesNotContain("PROJ-BRD-1", host.Markup));
    }

    [Fact]
    public async Task A_task_moved_by_someone_else_is_reported_with_its_new_board()
    {
        _taskAfterOpen = () => (HttpStatusCode.OK, _task with { BoardId = OtherBoardId, Number = 9 });
        var host = await OpenAsync();

        await host.InvokeAsync(() => _hub.RaiseEntityChanged(
            new EntityChangedEvent(EntityTypes.Task, _task.Id, ChangeKind.Deleted, [], TestData.OtherId)));

        host.WaitForAssertion(() => Assert.Contains(_changes,
            c => c is { Kind: TaskDrawerChangeKind.Moved, Task.Number: 9 } && c.Task.BoardId == OtherBoardId));
    }

    /// <summary>
    /// A task link followed out of the host drawer lands on a board page while
    /// the store still holds the drawer's board. The page must load its own
    /// board — the one in its URL — rather than trust the one it inherited.
    /// </summary>
    [Fact]
    public async Task A_board_page_opened_afterwards_shows_its_own_board()
    {
        await OpenAsync();

        Services.GetRequiredService<NavigationManager>().NavigateTo("projects/PROJ/boards/OTHER");
        var page = Render<Pages.Board>(p => p.Add(b => b.ProjectAlias, "PROJ").Add(b => b.BoardAlias, "OTHER"));

        page.WaitForAssertion(() => Assert.Contains("Other board", page.Markup));
        Assert.Equal(OtherBoardId, _store.BoardId);
    }

    [Fact]
    public async Task A_board_page_for_the_same_board_still_works_out_its_own_access()
    {
        await OpenAsync();

        Services.GetRequiredService<NavigationManager>().NavigateTo("projects/PROJ/boards/BRD");
        var page = Render<Pages.Board>(p => p.Add(b => b.ProjectAlias, "PROJ").Add(b => b.BoardAlias, "BRD"));

        // The header's create buttons render only for a writer, and only once
        // the page has worked out its access from the members it loaded.
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".board-header__actions")));
        Assert.Equal(TestData.BoardId, _store.BoardId);
        Assert.Contains("GET /api/projects/by-alias/PROJ/boards/BRD", _http.Paths);
    }
}
