using System.Net;
using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Solo.Client.Components;
using Solo.Client.Extensions;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests;

/// <summary>
/// The Your Tasks dashboard: one person's cards from every board, moved with
/// the board's own drag, kept current from core's user topic, with nothing
/// created on it.
/// </summary>
public class DashboardTests : ComponentTestBase
{
    private static readonly Guid OtherProjectId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OtherBoardId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly StubHttp _http = new();
    private readonly FakeBoardHub _hub = new();
    private readonly AssignedTasksStore _store;

    private static readonly ProjectSummaryDto Solo =
        new(TestData.ProjectId, "Solo", "SOLO", null, null, ProjectRole.ReadWrite, IsOwner: true);

    private static readonly ProjectSummaryDto Archive =
        new(OtherProjectId, "Archive", "ARCH", null, null, ProjectRole.Read, IsOwner: false);

    private static readonly BoardDto Main = new(TestData.BoardId, TestData.ProjectId, "Main", "MAIN", null, null, true, 1);
    private static readonly BoardDto Shelf = new(OtherBoardId, OtherProjectId, "Shelf", "SHELF", null, null, true, 1);

    private TaskDto _mine = TestData.Task(assignees: [TestData.MeId]) with { Name = "Rig the boss" };
    private TaskDto _readOnly = TestData.Task(id: Guid.NewGuid(), assignees: [TestData.MeId], status: TaskState.InProgress)
        with { Name = "Dust the shelf", BoardId = OtherBoardId };

    public DashboardTests()
    {
        JSInterop.SetupModule("./js/finished-column.js");
        var api = _http.CreateClient();
        var session = new UserSession(api);
        var topics = new LiveTopics(_hub, session, NullLogger<LiveTopics>.Instance);
        _store = new AssignedTasksStore(api, topics, session);

        Services.AddSingleton(api);
        Services.AddSingleton(session);
        Services.AddSingleton(_store);
        Services.AddSingleton(new BoardStore(api, _hub, session));
        Services.AddSingleton<IBoardHubClient>(_hub);
        Services.AddSingleton<ISoloLiveTopics>(topics);
        Services.AddScoped<TaskDrawerService>();
        Services.AddScoped<DragBridge>();
    }

    private AssignedTasksDto Payload() => new(
        [Solo, Archive], [Main, Shelf], [], [TestData.Member(TestData.MeId, "Hunter")], [_mine, _readOnly]);

    private void RouteServer()
    {
        // The tasks route first: the stub matches by substring, and "/api/users/me"
        // is a prefix of it.
        _http.RouteJson("/api/users/me/tasks", Payload());
        _http.RouteJson("/api/users/me", new UserDto(TestData.MeId, "h@x.io", "Hunter", null, false));
        _http.Route($"api/tasks/{_mine.Id}/status", HttpStatusCode.OK,
            () => _mine = _mine with { Status = TaskState.InProgress }, HttpMethod.Put);
        _http.Route($"api/tasks/{_mine.Id}", HttpStatusCode.OK, () => _mine);
    }

    private IRenderedComponent<Pages.Dashboard> RenderPage()
    {
        var page = Render<Pages.Dashboard>();
        page.WaitForAssertion(() => Assert.Contains("Rig the boss", page.Markup));
        return page;
    }

    [Fact]
    public void Cards_wear_their_board_as_a_tab_that_links_to_it_and_nothing_can_be_added()
    {
        RouteServer();
        var page = RenderPage();

        Assert.Contains("Your Tasks", page.Find("h1").TextContent);
        var tab = page.Find($".kanban-card-slot[data-drag-item='{_mine.Id}'] .kanban-card__origin");
        Assert.Equal("projects/SOLO/boards/MAIN", tab.GetAttribute("href"));
        Assert.Contains("Solo", tab.TextContent);
        Assert.Contains("Main", tab.TextContent);
        Assert.DoesNotContain("Add", page.FindAll("button").Select(b => b.TextContent.Trim()));
        Assert.Empty(page.FindAll(".kanban-lane__gutter"));
        Assert.Equal(2, page.FindAll(".kanban-card").Count);
    }

    [Fact]
    public void A_card_from_a_read_only_project_cannot_be_dragged()
    {
        RouteServer();
        var page = RenderPage();

        var slots = page.FindAll(".kanban-card-slot");
        Assert.Contains(slots, s => s.GetAttribute("data-drag-item") == _mine.Id.ToString());
        Assert.Contains(slots, s => !s.HasAttribute("data-drag-item") && s.TextContent.Contains("Dust the shelf"));
    }

    [Fact]
    public async Task Dropping_a_card_on_another_column_changes_its_status_on_its_own_board()
    {
        RouteServer();
        var page = RenderPage();
        var drag = Services.GetRequiredService<DragBridge>();

        await page.InvokeAsync(() => drag.OnDropAsync(
            new DragDrop("task", _mine.Id.ToString(), BoardZones.Id(null, TaskState.InProgress), 0)));

        Assert.Contains($"PUT /api/tasks/{_mine.Id}/status", _http.Paths);
        Assert.DoesNotContain(_http.Paths, p => p.Contains("/rank"));
        page.WaitForAssertion(() =>
        {
            var inProgress = page.FindAll(".kanban-zone")[1];
            Assert.Contains("Rig the boss", inProgress.TextContent);
        });
    }

    [Fact]
    public async Task A_nudge_on_the_user_topic_refetches_the_task_and_drops_one_that_is_no_longer_mine()
    {
        RouteServer();
        var page = RenderPage();
        Assert.Contains(BoardHubConstants.AssignedTasksTopic, _hub.UserSubscribed);

        _mine = _mine with { Name = "Rig the boss (renamed)", Version = 2 };
        await page.InvokeAsync(() => _hub.RaiseTopicMessage(new TopicMessage(
            Guid.Empty, BoardHubConstants.AssignedTasksTopic, EntityTypes.Task, _mine.Id.ToString(), TestData.OtherId)));
        page.WaitForAssertion(() => Assert.Contains("Rig the boss (renamed)", page.Markup));

        _mine = _mine with { AssigneeIds = [TestData.OtherId] };
        await page.InvokeAsync(() => _hub.RaiseTopicMessage(new TopicMessage(
            Guid.Empty, BoardHubConstants.AssignedTasksTopic, EntityTypes.Task, _mine.Id.ToString(), TestData.OtherId)));
        page.WaitForAssertion(() => Assert.DoesNotContain("Rig the boss", page.Markup));
    }

    [Fact]
    public async Task A_structural_nudge_reloads_the_whole_payload()
    {
        RouteServer();
        var page = RenderPage();
        var before = _http.Paths.Count(p => p == "GET /api/users/me/tasks");

        await page.InvokeAsync(() => _hub.RaiseTopicMessage(new TopicMessage(
            Guid.Empty, BoardHubConstants.AssignedTasksTopic, EntityTypes.Board, Main.Id.ToString(), TestData.OtherId)));
        await _store.PendingRefetch;

        Assert.Equal(before + 1, _http.Paths.Count(p => p == "GET /api/users/me/tasks"));
    }

    [Fact]
    public async Task What_the_drawer_deletes_leaves_the_board()
    {
        RouteServer();
        var page = RenderPage();

        var drawer = Services.GetRequiredService<TaskDrawerService>();
        await page.InvokeAsync(() => drawer.Report(new TaskDrawerChange(TaskDrawerChangeKind.Deleted, _mine.Id, null)));

        page.WaitForAssertion(() => Assert.DoesNotContain("Rig the boss", page.Markup));
    }

    [Fact]
    public void Nothing_assigned_shows_the_empty_state()
    {
        _http.RouteJson("/api/users/me/tasks", new AssignedTasksDto([], [], [], [], []));
        _http.RouteJson("/api/users/me", new UserDto(TestData.MeId, "h@x.io", "Hunter", null, false));

        var page = Render<Pages.Dashboard>();

        page.WaitForAssertion(() => Assert.Contains("Nothing is assigned to you.", page.Markup));
    }

    // -- Extension sections -----------------------------------------------------

    [Fact]
    public void An_extension_section_renders_above_the_board_and_folds_away()
    {
        RouteServer();
        Services.AddSingleton(new ExtensionCatalog
        {
            Loaded =
            [
                new LoadedExtension(
                    new ClientExtensionDto("fixture", "Fixture", "1.0.0", ">=1.13 <2.0", "Fixture", []),
                    Assembly.GetExecutingAssembly()),
            ],
        });
        var page = RenderPage();

        var header = page.Find(".dashboard-section__header");
        Assert.Contains("Your fixture roadmap", header.TextContent);
        Assert.Contains("fixture-dashboard-section", page.Markup);
        // The section sits above the board in document order.
        Assert.True(page.Markup.IndexOf("dashboard-section", StringComparison.Ordinal) <
                    page.Markup.IndexOf("kanban--personal", StringComparison.Ordinal));

        header.Click();

        page.WaitForAssertion(() => Assert.DoesNotContain("fixture-dashboard-section", page.Markup));
        Assert.Equal("false", page.Find(".dashboard-section__header").GetAttribute("aria-expanded"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.storage.set");
    }
}

[DashboardSection("Your fixture roadmap", Order = 5)]
public sealed class FixtureDashboardSection : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "fixture-dashboard-section");
        builder.AddContent(2, "A roadmap of your own");
        builder.CloseElement();
    }
}
