using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Roadmap.Client;
using Solo.Roadmap.Contracts;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Roadmap.Tests;

/// <summary>
/// The timeline page as a person's own roadmap on the dashboard: their tasks
/// from every project, moved on rows and days of their own, with the tasks'
/// dates left alone and a warning where a plan runs past a deadline.
/// </summary>
public class PersonalRoadmapPageTests : ComponentTestBase
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private readonly StubHttp _http = new();
    private readonly FakeLiveTopics _live = new();
    private readonly RaisingDrawer _drawer = new();
    private readonly FakeMenu _menu = new();
    private List<RoadmapEntryDto> _entries = [Mine("Save system", 1, "SOLO", "Solo"), Mine("Trailer cut", 2, "ART", "Art")];

    public PersonalRoadmapPageTests()
    {
        var client = _http.CreateClient();
        Services.AddSingleton<ISoloClientApi>(client);
        Services.AddSingleton<ISoloLiveTopics>(_live);
        Services.AddSingleton<ISoloTaskDrawer>(_drawer);
        Services.AddSingleton<ISoloTaskPicker>(new NeverPicker());
        Services.AddSingleton<ISoloContextMenu>(_menu);
        JSInterop.SetupModule(new RoadmapApi(client).Content("roadmap.js").ToString());

        _http.Route("api/users/me", HttpStatusCode.OK,
            () => new UserDto(TestData.MeId, "me@example.com", "Me", null, false));
        _http.Route("api/me/tasks", HttpStatusCode.OK, () => new AssignedTasksDto([], [], [],
            [new ProjectMemberDto(Guid.NewGuid(), TestData.MeId, "me@example.com", "Me", null, ProjectRole.ReadWrite, false, false)],
            []));
        _http.RouteJson("/labels", Array.Empty<LabelDto>());
        _http.Route("api/ext/roadmap/me?", HttpStatusCode.OK, () => new RoadmapTimelineDto(
            Guid.Empty, Guid.Empty, "", "Your Roadmap", 0,
            new RoadmapSettingsDto(30, 60, DayOfWeek.Monday, 2, 1), _entries.ToList(), []), HttpMethod.Get);
        _http.Route("/me/entries/", HttpStatusCode.NoContent, (object?)null, HttpMethod.Put);
        _http.Route("/me/entries/", HttpStatusCode.NoContent, (object?)null, HttpMethod.Delete);
    }

    /// <summary>A task of the signed-in person's, due in three days, from the project and board given.</summary>
    private static RoadmapEntryDto Mine(string name, int number, string projectAlias, string projectName) =>
        new(Guid.NewGuid(), number * 1024m, Today, "FEAT", "Features",
            TestData.Task(number: number, deadline: Today.AddDays(3), assignees: [TestData.MeId]) with { Name = name },
            projectAlias, projectName);

    private IRenderedComponent<RoadmapPage> RenderPage()
    {
        var page = Render<RoadmapPage>(p => p.Add(r => r.Personal, true));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".roadmap-timeline")));
        return page;
    }

    private int TimelineReads => _http.Paths.Count(p => p.StartsWith("GET /api/ext/roadmap/me?"));

    private List<string> Requests(string method, string fragment) =>
        _http.Paths.Select((path, i) => (path, body: _http.Bodies[i]))
            .Where(r => r.path.StartsWith($"{method} ") && r.path.Contains(fragment))
            .Select(r => r.path + " " + r.body)
            .ToList();

    private static AngleSharp.Dom.IElement Bar(IRenderedComponent<RoadmapPage> page, RoadmapEntryDto entry) =>
        page.Find($"[data-roadmap-entry='{entry.Id}'] .roadmap-bar");

    [Fact]
    public void It_draws_the_persons_tasks_without_a_header_and_serials_them_by_their_own_project()
    {
        var page = RenderPage();

        Assert.Empty(page.FindAll(".roadmap-header"));
        Assert.Empty(page.FindAll(".roadmap-page .mud-button"));
        Assert.Equal(2, page.FindAll(".roadmap-bar").Count);
        Assert.Equal("SOLO-FEAT-1", page.Find(".roadmap-bar .roadmap-bar__serial").TextContent.Trim());
        Assert.Contains("ART-FEAT-2", Bar(page, _entries[1]).TextContent);
        Assert.Contains("Art · Features", Bar(page, _entries[1]).GetAttribute("title"));
        // Its own topic for rows moved elsewhere, core's for the tasks themselves.
        Assert.Contains((Guid.Empty, RoadmapTopics.Personal), _live.Held);
        Assert.Contains((Guid.Empty, "solo:assigned-tasks"), _live.Held);
        Assert.Empty(_live.Reported);
    }

    [Fact]
    public void With_nothing_assigned_it_says_so()
    {
        _entries = [];

        var page = RenderPage();

        Assert.Contains("No tasks are assigned to you", page.Find(".roadmap-empty").TextContent);
    }

    [Fact]
    public async Task Dragging_a_bar_plans_it_here_and_leaves_the_tasks_dates_alone()
    {
        var page = RenderPage();
        var entry = _entries[0];
        var before = Bar(page, entry).ParentElement!.GetAttribute("data-start");

        await page.Instance.OnMove(entry.Id.ToString(), 4);

        page.WaitForAssertion(() => Assert.Single(Requests("PUT", $"/me/entries/{entry.Id}/schedule")));
        var plan = Requests("PUT", $"/me/entries/{entry.Id}/schedule")[0];
        Assert.Contains($"\"{Today.AddDays(4):yyyy-MM-dd}\"", plan);
        Assert.Contains($"\"{Today.AddDays(7):yyyy-MM-dd}\"", plan);
        Assert.Empty(Requests("PUT", "api/tasks/"));
        Assert.Equal(int.Parse(before!) + 4, int.Parse(Bar(page, entry).ParentElement!.GetAttribute("data-start")!));
    }

    [Fact]
    public async Task Resizing_an_edge_plans_it_here_too()
    {
        var page = RenderPage();
        var entry = _entries[0];

        await page.Instance.OnResize(entry.Id.ToString(), "end", 30 + 9);

        page.WaitForAssertion(() => Assert.Single(Requests("PUT", $"/me/entries/{entry.Id}/schedule")));
        Assert.Contains($"\"{Today.AddDays(9):yyyy-MM-dd}\"", Requests("PUT", "/schedule")[0]);
        Assert.Empty(Requests("PUT", "api/tasks/"));
    }

    /// <summary>
    /// The warning and the hatching are positioned inside the bar rather than
    /// laid out in it, so the label's place is the same with and without them.
    /// </summary>
    [Fact]
    public void A_bar_planned_past_its_deadline_is_marked_without_moving_anything()
    {
        var late = _entries[0] with { Schedule = new RoadmapScheduleDto(Today, Today.AddDays(5)) };
        var inTime = _entries[1] with { Schedule = new RoadmapScheduleDto(Today.AddDays(-2), Today.AddDays(3)) };
        _entries = [late, inTime];

        var page = RenderPage();

        var bar = Bar(page, late);
        Assert.Contains("roadmap-bar--overrun", bar.ClassName);
        Assert.Contains("--bar-overrun:160px", bar.GetAttribute("style"));
        Assert.Contains($"planned 2 days past its deadline, {Dates.Day(Today.AddDays(3))}", bar.GetAttribute("title"));
        var warning = bar.QuerySelector(".roadmap-bar__overrun");
        Assert.NotNull(warning);
        Assert.DoesNotContain("roadmap-bar--overrun", Bar(page, inTime).ClassName);
        Assert.Null(Bar(page, inTime).QuerySelector(".roadmap-bar__overrun"));
        // Same label, same place in the flow, overrun or not.
        Assert.Equal(
            Bar(page, inTime).Children.TakeWhile(c => !c.ClassList.Contains("roadmap-bar__label")).Count(),
            bar.Children.TakeWhile(c => !c.ClassList.Contains("roadmap-bar__label")).Count());
    }

    [Fact]
    public async Task The_bar_menu_opens_goes_to_the_board_or_puts_a_planned_bar_back_on_its_dates()
    {
        var planned = _entries[0] with { Schedule = new RoadmapScheduleDto(Today.AddDays(1), Today.AddDays(2)) };
        _entries = [planned, _entries[1]];
        var page = RenderPage();

        _menu.Next = null;
        await page.Instance.OnBarMenu(_entries[1].Id.ToString(), 10, 10);
        Assert.Equal(["Open task", "Go to Features"], _menu.Labels());

        _menu.Next = "follow";
        await page.Instance.OnBarMenu(planned.Id.ToString(), 10, 10);
        Assert.Equal(["Open task", "Go to Features", "Use the task's dates"], _menu.Labels());
        page.WaitForAssertion(() => Assert.Single(Requests("DELETE", $"/me/entries/{planned.Id}/schedule")));
        Assert.DoesNotContain("roadmap-bar--overrun", Bar(page, planned).ClassName);
        Assert.Equal(_window(Today).ToString(), Bar(page, planned).ParentElement!.GetAttribute("data-start"));
    }

    /// <summary>The window opens 30 days back (PastDays), so today is column 30.</summary>
    private static int _window(DateOnly day) => 30 + day.DayNumber - Today.DayNumber;

    [Fact]
    public async Task Nothing_is_added_or_removed_from_here()
    {
        var page = RenderPage();

        await page.Instance.OnDayMenu(31, 0, 10, 10);
        await page.Instance.OnMarquee([_entries[0].Id.ToString()], additive: false);
        await page.Instance.OnRemoveSelected();

        Assert.Empty(_menu.Shown);
        Assert.DoesNotContain(_http.Paths, p => p.StartsWith("POST ") || p.StartsWith("DELETE "));
        Assert.Equal(2, page.FindAll(".roadmap-bar").Count);
    }

    [Fact]
    public async Task Rows_are_moved_through_the_persons_own_endpoints()
    {
        var page = RenderPage();

        await page.Instance.OnRowDrop(_entries[1].Id.ToString(), 0, join: false);

        page.WaitForAssertion(() => Assert.Single(Requests("PUT", $"/me/entries/{_entries[1].Id}/rank")));
        Assert.DoesNotContain(_http.Paths, p => p.Contains("/projects/"));
    }

    [Fact]
    public async Task A_change_to_one_of_their_tasks_anywhere_re_reads_the_roadmap()
    {
        var page = RenderPage();
        page.WaitForAssertion(() => Assert.Equal(1, TimelineReads));
        _entries = [_entries[0]];

        await _live.SendAsync(Guid.Empty, "solo:assigned-tasks", EntityTypes.Task, _entries[0].Task.Id.ToString());

        page.WaitForAssertion(() => Assert.Equal(2, TimelineReads));
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".roadmap-bar")));
    }

    [Fact]
    public void A_task_handed_to_someone_else_in_the_drawer_leaves_the_roadmap()
    {
        var page = RenderPage();
        var entry = _entries[0];

        _drawer.Raise(new TaskDrawerChange(TaskDrawerChangeKind.Saved, entry.Task.Id,
            entry.Task with { AssigneeIds = [TestData.OtherId] }));

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".roadmap-bar")));
        Assert.Equal("Trailer cut", page.Find(".roadmap-bar__name").TextContent.Trim());
    }

    private sealed class FakeMenu : ISoloContextMenu
    {
        public string? Next { get; set; }

        public List<IReadOnlyList<ContextMenuItem>> Shown { get; } = [];

        public List<string> Labels() => Shown[^1].Select(i => i.Label).ToList();

        public Task<string?> ShowAsync(
            double clientX, double clientY, IReadOnlyList<ContextMenuItem> items, CancellationToken ct = default)
        {
            Shown.Add(items);
            return Task.FromResult(Next);
        }
    }

    private sealed class NeverPicker : ISoloTaskPicker
    {
        public Task<TaskDto?> PickAsync(Guid projectId, TaskPickerOptions? options = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("A person's own roadmap never picks tasks.");
    }

    private sealed class RaisingDrawer : ISoloTaskDrawer
    {
        public Task OpenAsync(Guid taskId, CancellationToken ct = default) => Task.CompletedTask;

        public event Action<TaskDrawerChange>? Changed;

        public event Action? Closed
        {
            add { }
            remove { }
        }

        public void Raise(TaskDrawerChange change) => Changed?.Invoke(change);
    }
}
