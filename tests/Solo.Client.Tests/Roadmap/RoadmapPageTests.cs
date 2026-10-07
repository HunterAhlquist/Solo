using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Roadmap.Client;
using Solo.Roadmap.Contracts;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests.Roadmap;

/// <summary>
/// The roadmap page keeping up with everyone else: their changes, and who else
/// has it open. The grid's gestures are roadmap.js's, so a test plays its part
/// by calling the page's JS callbacks directly.
/// </summary>
public class RoadmapPageTests : ComponentTestBase
{
    private static readonly Guid ProjectId = TestData.ProjectId;
    private static readonly Guid RoadmapId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly string Topic = RoadmapTopics.Roadmap(RoadmapId);

    private readonly StubHttp _http = new();
    private readonly FakeLiveTopics _live = new();
    private readonly FakePicker _picker = new();
    private readonly FakeMenu _menu = new();
    private List<RoadmapEntryDto> _entries = [Entry("Save system", 1)];
    private List<RoadmapMilestoneDto> _milestones = [];
    private ProjectRole _role = ProjectRole.ReadWrite;

    public RoadmapPageTests()
    {
        var client = _http.CreateClient();
        Services.AddSingleton<ISoloClientApi>(client);
        Services.AddSingleton<ISoloLiveTopics>(_live);
        Services.AddSingleton<ISoloTaskDrawer>(new IdleDrawer());
        Services.AddSingleton<ISoloTaskPicker>(_picker);
        Services.AddSingleton<ISoloContextMenu>(_menu);
        JSInterop.SetupModule(new RoadmapApi(client).Content("roadmap.js").ToString());

        _http.RouteJson("/members", new[]
        {
            new ProjectMemberDto(Guid.NewGuid(), TestData.OtherId, "riley@example.com", "Riley Park", null,
                ProjectRole.ReadWrite, false, false),
        });
        _http.RouteJson("/labels", Array.Empty<LabelDto>());
        _http.Route($"roadmaps/{RoadmapId}", HttpStatusCode.OK, () => Timeline(_entries, _milestones), HttpMethod.Get);
        _http.Route("/api/projects", HttpStatusCode.OK, () => new[]
        {
            new ProjectSummaryDto(ProjectId, "Solo", "SOLO", null, null, _role, false),
        });
    }

    private static RoadmapEntryDto Entry(string name, int number) =>
        new(Guid.NewGuid(), number * 1024m, DateOnly.FromDateTime(DateTime.Today), "FEAT", "Features",
            TestData.Task(number: number, deadline: DateOnly.FromDateTime(DateTime.Today).AddDays(3)) with { Name = name });

    private static RoadmapTimelineDto Timeline(
        IEnumerable<RoadmapEntryDto> entries, IEnumerable<RoadmapMilestoneDto> milestones) =>
        new(RoadmapId, ProjectId, "SOLO", "2027 Launch", 1,
            new RoadmapSettingsDto(30, 60, DayOfWeek.Monday, 2, 1), entries.ToList(), milestones.ToList());

    private IRenderedComponent<RoadmapPage> RenderPage()
    {
        var page = Render<RoadmapPage>(p => p
            .Add(r => r.ProjectAlias, "SOLO")
            .Add(r => r.RoadmapId, RoadmapId));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".roadmap-header")));
        return page;
    }

    private int TimelineReads => _http.Paths.Count(p => p == $"GET /api/ext/roadmap/projects/{ProjectId}/roadmaps/{RoadmapId}");

    private static List<string> Names(IRenderedComponent<RoadmapPage> page) =>
        page.FindAll(".roadmap-bar .roadmap-bar__text:not(.roadmap-bar__text--copy) .roadmap-bar__name")
            .Select(n => n.TextContent.Trim())
            .ToList();

    [Fact]
    public void Opening_a_roadmap_joins_its_topic_as_a_viewer()
    {
        RenderPage();

        Assert.Contains((ProjectId, Topic), _live.Held);
        Assert.Equal([(Topic, PresenceMode.Viewing)], _live.Reported);
    }

    [Fact]
    public async Task Whoever_else_is_here_shows_in_the_header_editors_first_and_marked()
    {
        var page = RenderPage();

        await _live.PresentAsync(ProjectId, Topic,
            new TopicPresenceEntry(Guid.NewGuid(), "Alex Moore", PresenceMode.Viewing),
            new TopicPresenceEntry(TestData.OtherId, "Riley Park", PresenceMode.Editing));

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".roadmap-presence .mud-avatar").Count));
        var avatars = page.FindAll(".roadmap-presence .mud-avatar");
        Assert.Contains("roadmap-presence--editing", avatars[0].ClassName);
        Assert.Equal("RP", avatars[0].TextContent.Trim());
        Assert.DoesNotContain("roadmap-presence--editing", avatars[1].ClassName);
        Assert.Equal("AM", avatars[1].TextContent.Trim());

        await _live.PresentAsync(ProjectId, Topic);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".roadmap-presence")));
    }

    [Fact]
    public async Task A_change_someone_else_makes_redraws_the_rows()
    {
        var page = RenderPage();
        Assert.Equal(["Save system"], Names(page));

        _entries = [.._entries, Entry("Photo mode", 2)];
        await _live.SendAsync(ProjectId, Topic, RoadmapTopics.EntriesChanged);

        page.WaitForAssertion(() => Assert.Equal(["Save system", "Photo mode"], Names(page)));
    }

    [Fact]
    public async Task A_change_heard_mid_drag_waits_for_the_drop_and_the_drag_reads_as_editing()
    {
        var page = RenderPage();
        var reads = TimelineReads;

        await page.Instance.OnGesture(true);
        _entries = [.._entries, Entry("Photo mode", 2)];
        await _live.SendAsync(ProjectId, Topic, RoadmapTopics.TaskChanged, Guid.NewGuid().ToString());
        await _live.SendAsync(ProjectId, Topic, RoadmapTopics.EntriesChanged);

        Assert.Equal(reads, TimelineReads);
        Assert.Equal(["Save system"], Names(page));
        Assert.Equal((Topic, PresenceMode.Editing), _live.Reported[^1]);

        await page.Instance.OnGesture(false);

        page.WaitForAssertion(() => Assert.Equal(["Save system", "Photo mode"], Names(page)));
        Assert.Equal(reads + 1, TimelineReads);
        Assert.Equal((Topic, PresenceMode.Viewing), _live.Reported[^1]);
    }

    [Fact]
    public async Task A_roadmap_deleted_by_someone_else_says_so_and_stops_listening()
    {
        var page = RenderPage();

        await _live.SendAsync(ProjectId, Topic, RoadmapTopics.Deleted);

        page.WaitForAssertion(() => Assert.Contains("Someone deleted this roadmap", page.Markup));
        Assert.Empty(page.FindAll(".roadmap-timeline"));
        Assert.Empty(_live.Held);
    }

    [Fact]
    public async Task Leaving_the_page_leaves_the_topic()
    {
        RenderPage();

        await DisposeComponentsOnDispatcherAsync();

        Assert.Empty(_live.Held);
    }

    [Fact]
    public async Task Add_task_offers_the_host_picker_without_whats_on_the_roadmap_and_adds_the_pick()
    {
        var added = Entry("Photo mode", 2);
        _http.Route($"roadmaps/{RoadmapId}/entries", HttpStatusCode.OK, added, HttpMethod.Post);
        _picker.Next = added.Task;
        var page = RenderPage();
        PresenceMode? whilePicking = null;
        _picker.During = () => whilePicking = _live.Reported[^1].Mode;

        AddTaskButton(page).Click();

        page.WaitForAssertion(() => Assert.Equal(["Save system", "Photo mode"], Names(page)));
        var (project, options) = Assert.Single(_picker.Calls);
        Assert.Equal(ProjectId, project);
        Assert.Equal([_entries[0].Task.Id], options!.Exclude);
        Assert.StartsWith("Add a task on ", options.Title);
        Assert.Equal(PresenceMode.Editing, whilePicking);
        Assert.Equal((Topic, PresenceMode.Viewing), _live.Reported[^1]);
        Assert.Contains(added.Task.Id.ToString(), _http.Bodies[_http.Paths.IndexOf(
            $"POST /api/ext/roadmap/projects/{ProjectId}/roadmaps/{RoadmapId}/entries")]);
    }

    [Fact]
    public void Cancelling_the_picker_adds_nothing()
    {
        var page = RenderPage();

        AddTaskButton(page).Click();

        page.WaitForAssertion(() => Assert.Equal((Topic, PresenceMode.Viewing), _live.Reported[^1]));
        Assert.Single(_picker.Calls);
        Assert.DoesNotContain(_http.Paths, p => p.StartsWith("POST "));
        Assert.Equal(["Save system"], Names(page));
    }

    [Fact]
    public async Task Right_clicking_a_day_offers_to_add_a_task_on_that_day()
    {
        var day = DateOnly.FromDateTime(DateTime.Today).AddDays(2);
        var added = Entry("Photo mode", 2);
        _http.Route($"roadmaps/{RoadmapId}/entries", HttpStatusCode.OK, added, HttpMethod.Post);
        _picker.Next = added.Task;
        _menu.Next = "add";
        var page = RenderPage();

        // The window opens 30 days back (PastDays), so column 32 is two days on.
        await page.Instance.OnDayMenu(32, 100, 200);

        Assert.Equal([$"Add a task on {Dates.Day(day)}", $"Add a milestone on {Dates.Day(day)}"], _menu.Labels());
        page.WaitForAssertion(() => Assert.Equal(["Save system", "Photo mode"], Names(page)));
        Assert.Equal($"Add a task on {Dates.Day(day)}", Assert.Single(_picker.Calls).Options!.Title);
        Assert.Contains($"\"{day:yyyy-MM-dd}\"", Body("POST", "/entries"));
    }

    [Fact]
    public async Task Dismissing_the_day_menu_adds_nothing()
    {
        var page = RenderPage();

        await page.Instance.OnDayMenu(32, 100, 200);

        Assert.Single(_menu.Shown);
        Assert.Empty(_picker.Calls);
    }

    [Fact]
    public async Task A_bars_menu_opens_it_or_takes_it_off_the_roadmap()
    {
        _http.Route($"roadmaps/{RoadmapId}/entries/remove", HttpStatusCode.NoContent, (object?)null, HttpMethod.Post);
        _menu.Next = "remove";
        var page = RenderPage();
        var id = _entries[0].Id;

        await page.Instance.OnBarMenu(id.ToString(), 10, 10);

        Assert.Equal(["Open task", "Remove from roadmap"], _menu.Labels());
        Assert.True(_menu.Shown[0][1].Danger);
        page.WaitForAssertion(() => Assert.Empty(Names(page)));
        Assert.Contains(id.ToString(), Body("POST", "/entries/remove"));
    }

    [Fact]
    public async Task Selected_bars_come_off_the_roadmap_together_in_one_request()
    {
        _entries = [Entry("Save system", 1), Entry("Photo mode", 2), Entry("Boss fight", 3)];
        _http.Route($"roadmaps/{RoadmapId}/entries/remove", HttpStatusCode.NoContent, (object?)null, HttpMethod.Post);
        var page = RenderPage();
        var (first, middle, last) = (_entries[0].Id, _entries[1].Id, _entries[2].Id);

        await page.Instance.OnBarSelect(first.ToString(), range: false);
        await page.Instance.OnBarSelect(last.ToString(), range: false);
        Assert.Equal(["Save system", "Boss fight"], Selected(page));

        _menu.Next = "remove";
        await page.Instance.OnBarMenu(last.ToString(), 10, 10);

        // Nothing to open when there's more than one.
        Assert.Equal(["Remove 2 tasks from roadmap"], _menu.Labels());
        page.WaitForAssertion(() => Assert.Equal(["Photo mode"], Names(page)));
        Assert.Single(_http.Paths, p => p.EndsWith("/entries/remove"));
        var body = Body("POST", "/entries/remove");
        Assert.Contains(first.ToString(), body);
        Assert.Contains(last.ToString(), body);
        Assert.DoesNotContain(middle.ToString(), body);
    }

    [Fact]
    public async Task A_failed_remove_leaves_the_bars_where_they_are()
    {
        _http.Route($"roadmaps/{RoadmapId}/entries/remove", HttpStatusCode.InternalServerError, (object?)null, HttpMethod.Post);
        _menu.Next = "remove";
        var page = RenderPage();

        await page.Instance.OnBarMenu(_entries[0].Id.ToString(), 10, 10);

        Assert.Equal(["Save system"], Names(page));
        Assert.Equal(["Save system"], Selected(page));
    }

    [Fact]
    public async Task Right_clicking_a_bar_outside_the_selection_selects_that_bar_alone()
    {
        _entries = [Entry("Save system", 1), Entry("Photo mode", 2), Entry("Boss fight", 3)];
        var page = RenderPage();
        await page.Instance.OnMarquee([_entries[0].Id.ToString(), _entries[1].Id.ToString()], additive: false);
        Assert.Equal(["Save system", "Photo mode"], Selected(page));

        await page.Instance.OnBarMenu(_entries[2].Id.ToString(), 10, 10);

        Assert.Equal(["Boss fight"], Selected(page));
        Assert.Equal(["Open task", "Remove from roadmap"], _menu.Labels());
    }

    [Fact]
    public async Task Shift_click_selects_every_row_from_the_last_one_clicked()
    {
        _entries = [Entry("Save system", 1), Entry("Photo mode", 2), Entry("Boss fight", 3)];
        var page = RenderPage();

        await page.Instance.OnBarSelect(_entries[2].Id.ToString(), range: false);
        await page.Instance.OnBarSelect(_entries[0].Id.ToString(), range: true);

        Assert.Equal(["Save system", "Photo mode", "Boss fight"], Selected(page));
    }

    [Fact]
    public async Task A_box_replaces_the_selection_unless_a_modifier_was_held()
    {
        _entries = [Entry("Save system", 1), Entry("Photo mode", 2), Entry("Boss fight", 3)];
        var page = RenderPage();
        await page.Instance.OnBarSelect(_entries[0].Id.ToString(), range: false);

        await page.Instance.OnMarquee([_entries[2].Id.ToString()], additive: false);
        Assert.Equal(["Boss fight"], Selected(page));

        await page.Instance.OnMarquee([_entries[1].Id.ToString()], additive: true);
        Assert.Equal(["Photo mode", "Boss fight"], Selected(page));
    }

    [Fact]
    public async Task Clicking_the_grid_or_a_bar_clears_the_selection()
    {
        _entries = [Entry("Save system", 1), Entry("Photo mode", 2)];
        var page = RenderPage();
        var both = _entries.Select(e => e.Id.ToString()).ToArray();

        await page.Instance.OnMarquee(both, additive: false);
        await page.Instance.OnClearSelection();
        Assert.Empty(Selected(page));

        await page.Instance.OnMarquee(both, additive: false);
        await page.Instance.OnBarClick(both[1]);
        Assert.Empty(Selected(page));
    }

    [Fact]
    public async Task Someone_else_removing_a_selected_bar_takes_it_out_of_the_selection()
    {
        _entries = [Entry("Save system", 1), Entry("Photo mode", 2)];
        var page = RenderPage();
        await page.Instance.OnMarquee(_entries.Select(e => e.Id.ToString()).ToArray(), additive: false);

        _entries = [_entries[1]];
        await _live.SendAsync(ProjectId, Topic, RoadmapTopics.EntriesChanged);
        page.WaitForAssertion(() => Assert.Equal(["Photo mode"], Names(page)));

        _menu.Next = null;
        await page.Instance.OnBarMenu(_entries[0].Id.ToString(), 10, 10);
        Assert.Equal(["Open task", "Remove from roadmap"], _menu.Labels());
    }

    [Fact]
    public async Task A_read_only_member_can_open_a_task_from_its_menu_but_select_and_remove_nothing()
    {
        _role = ProjectRole.Read;
        var page = RenderPage();
        var id = _entries[0].Id.ToString();

        await page.Instance.OnBarSelect(id, range: false);
        await page.Instance.OnMarquee([id], additive: false);
        await page.Instance.OnBarMenu(id, 10, 10);
        await page.Instance.OnDayMenu(32, 10, 10);

        Assert.Empty(Selected(page));
        Assert.Equal(["Open task"], _menu.Labels());
        Assert.Single(_menu.Shown);
    }

    // -- Edge hints --------------------------------------------------------------------

    [Fact]
    public void A_finished_task_that_ended_before_today_gets_no_edge_hints()
    {
        var done = Entry("Title screen", 2);
        done = done with
        {
            Task = done.Task with { Status = TaskState.Finished, StartAt = Today.AddDays(-9), DeadlineAt = Today.AddDays(-2) },
        };
        // Finished, but its last day is today: still in flight as far as the view goes.
        var endsToday = Entry("Credits", 3);
        endsToday = endsToday with { Task = endsToday.Task with { Status = TaskState.Finished, DeadlineAt = Today } };
        _entries = [Entry("Save system", 1), done, endsToday];

        var page = RenderPage();

        int Hints(RoadmapEntryDto entry) =>
            page.Find($"[data-roadmap-entry='{entry.Id}']").QuerySelectorAll("[data-roadmap-hint]").Length;
        Assert.Equal(2, Hints(_entries[0]));
        Assert.Equal(0, Hints(done));
        Assert.Equal(2, Hints(endsToday));
    }

    // -- Moving a selection together --------------------------------------------------

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static RoadmapEntryDto Pill(string name, int number) =>
        Entry(name, number) with { Task = Entry(name, number).Task with { DeadlineAt = null } };

    /// <summary>Answers core's schedule and the roadmap's anchor endpoints, as a save that went through.</summary>
    private void RouteMoves()
    {
        foreach (var entry in _entries)
        {
            _http.Route($"api/tasks/{entry.Task.Id}/schedule", HttpStatusCode.OK, entry.Task, HttpMethod.Put);
        }

        _http.Route("/anchor", HttpStatusCode.NoContent, (object?)null, HttpMethod.Put);
    }

    private List<string> Requests(string method, string fragment) =>
        _http.Paths.Select((path, i) => (path, body: _http.Bodies[i]))
            .Where(r => r.path.StartsWith($"{method} ") && r.path.Contains(fragment))
            .Select(r => r.path + " " + r.body)
            .ToList();

    [Fact]
    public async Task Dragging_a_selected_bar_sideways_moves_every_selected_bar_the_same_days()
    {
        _entries = [Entry("Save system", 1), Entry("Photo mode", 2), Pill("Boss fight", 3)];
        RouteMoves();
        var page = RenderPage();
        var (dated, untouched, pill) = (_entries[0], _entries[1], _entries[2]);
        await page.Instance.OnMarquee([dated.Id.ToString(), pill.Id.ToString()], additive: false);

        await page.Instance.OnMove(dated.Id.ToString(), 2);

        page.WaitForAssertion(() => Assert.Equal(2, _http.Paths.Count(p => p.StartsWith("PUT "))));
        var schedule = Assert.Single(Requests("PUT", $"api/tasks/{dated.Task.Id}/schedule"));
        Assert.Contains($"\"{Today.AddDays(2):yyyy-MM-dd}\"", schedule);
        Assert.Contains($"\"{Today.AddDays(5):yyyy-MM-dd}\"", schedule);
        var anchor = Assert.Single(Requests("PUT", $"entries/{pill.Id}/anchor"));
        Assert.Contains($"\"{Today.AddDays(2):yyyy-MM-dd}\"", anchor);
        Assert.Empty(Requests("PUT", $"api/tasks/{untouched.Task.Id}"));
    }

    [Fact]
    public async Task A_selection_moved_sideways_stops_together_where_its_first_bar_meets_the_edge()
    {
        // The window ends 60 days out (FutureDays), so the far bar has two to go.
        var far = Entry("Photo mode", 2) with { Task = Entry("Photo mode", 2).Task with { DeadlineAt = Today.AddDays(58) } };
        _entries = [Entry("Save system", 1), far];
        RouteMoves();
        var page = RenderPage();
        await page.Instance.OnMarquee(_entries.Select(e => e.Id.ToString()).ToArray(), additive: false);

        await page.Instance.OnMove(_entries[0].Id.ToString(), 10);

        page.WaitForAssertion(() => Assert.Equal(2, Requests("PUT", "/schedule").Count));
        Assert.Contains($"\"{Today.AddDays(5):yyyy-MM-dd}\"", Assert.Single(Requests("PUT", $"{_entries[0].Task.Id}/schedule")));
        Assert.Contains($"\"{Today.AddDays(60):yyyy-MM-dd}\"", Assert.Single(Requests("PUT", $"{far.Task.Id}/schedule")));
    }

    [Fact]
    public async Task Dragging_a_bar_outside_the_selection_moves_it_alone()
    {
        _entries = [Entry("Save system", 1), Entry("Photo mode", 2), Entry("Boss fight", 3)];
        RouteMoves();
        var page = RenderPage();
        await page.Instance.OnMarquee([_entries[0].Id.ToString(), _entries[1].Id.ToString()], additive: false);

        await page.Instance.OnMove(_entries[2].Id.ToString(), 1);

        page.WaitForAssertion(() => Assert.Single(Requests("PUT", "/schedule")));
        Assert.Single(Requests("PUT", $"{_entries[2].Task.Id}/schedule"));
    }

    [Fact]
    public async Task Dropping_a_selected_row_lands_the_whole_selection_there_in_order()
    {
        _entries = [Entry("Save system", 1), Entry("Photo mode", 2), Entry("Boss fight", 3), Entry("Credits", 4)];
        _http.Route($"roadmaps/{RoadmapId}/entries/rank", HttpStatusCode.NoContent, (object?)null, HttpMethod.Put);
        var page = RenderPage();
        var (first, third, last) = (_entries[0], _entries[2], _entries[3]);
        await page.Instance.OnMarquee([first.Id.ToString(), third.Id.ToString()], additive: false);
        // What roadmap.js reads to drag the rows together.
        Assert.Equal([first.Id.ToString(), third.Id.ToString()],
            page.FindAll(".roadmap-row[data-selected]").Select(r => r.GetAttribute("data-roadmap-entry")));

        // The first row dragged below the last: index 3 among the other three.
        await page.Instance.OnRowDrop(first.Id.ToString(), 3);

        Assert.Equal(["Photo mode", "Credits", "Save system", "Boss fight"], Names(page));
        var body = Assert.Single(Requests("PUT", "/entries/rank"));
        Assert.Contains(first.Id.ToString(), body);
        Assert.Contains(third.Id.ToString(), body);
        Assert.Contains($"\"precedingId\":\"{last.Id}\"", body);
        Assert.Contains("\"followingId\":null", body);
    }

    [Fact]
    public async Task A_failed_group_drop_puts_the_rows_back()
    {
        _entries = [Entry("Save system", 1), Entry("Photo mode", 2), Entry("Boss fight", 3)];
        _http.Route($"roadmaps/{RoadmapId}/entries/rank", HttpStatusCode.InternalServerError, (object?)null, HttpMethod.Put);
        var page = RenderPage();
        await page.Instance.OnMarquee([_entries[0].Id.ToString(), _entries[1].Id.ToString()], additive: false);

        await page.Instance.OnRowDrop(_entries[0].Id.ToString(), 2);

        Assert.Equal(["Save system", "Photo mode", "Boss fight"], Names(page));
    }

    [Fact]
    public async Task The_delete_key_takes_the_selection_off_the_roadmap()
    {
        _entries = [Entry("Save system", 1), Entry("Photo mode", 2), Entry("Boss fight", 3)];
        _http.Route($"roadmaps/{RoadmapId}/entries/remove", HttpStatusCode.NoContent, (object?)null, HttpMethod.Post);
        var page = RenderPage();

        await page.Instance.OnRemoveSelected();
        Assert.Empty(Requests("POST", "/entries/remove"));

        await page.Instance.OnMarquee([_entries[0].Id.ToString(), _entries[2].Id.ToString()], additive: false);
        await page.Instance.OnRemoveSelected();

        page.WaitForAssertion(() => Assert.Equal(["Photo mode"], Names(page)));
        Assert.Single(Requests("POST", "/entries/remove"));
    }

    // -- Milestones -------------------------------------------------------------------

    private static RoadmapMilestoneDto Milestone(string name, int daysFromToday, uint version = 7) =>
        new(Guid.NewGuid(), name, Today.AddDays(daysFromToday), "#1e88e5", version);

    private static List<string> Flags(IRenderedComponent<RoadmapPage> page) =>
        page.FindAll(".roadmap-milestone__flag").Select(f => f.TextContent.Trim()).ToList();

    [Fact]
    public void A_milestone_is_a_flag_in_the_header_and_a_line_through_the_grid_at_the_end_of_its_day()
    {
        _milestones = [Milestone("Beta", 3)];
        var page = RenderPage();

        Assert.Equal(["Beta"], Flags(page));
        // The window opens 30 days back, so its day is column 33 and its end 34 columns in.
        var line = page.Find(".roadmap-milestone-line");
        Assert.Contains("--milestone-x:1360px", line.GetAttribute("style"));
        Assert.Contains("--milestone:#1e88e5", line.GetAttribute("style"));
        Assert.Contains("--milestone-ink:#fff", line.GetAttribute("style"));
        Assert.Equal(_milestones[0].Id.ToString(), line.GetAttribute("data-roadmap-milestone"));
        Assert.Equal("33", line.GetAttribute("data-day"));
    }

    [Fact]
    public void A_milestone_due_this_sprint_flashes_and_one_passed_or_sprints_away_does_not()
    {
        // Two-week sprints: one forty days out is at least a sprint away whatever today is.
        _milestones = [Milestone("Yesterday", -1), Milestone("Today", 0), Milestone("Later", 40)];
        var page = RenderPage();

        var due = page.FindAll(".roadmap-milestone.roadmap-milestone--due .roadmap-milestone__flag")
            .Select(f => f.TextContent.Trim());
        Assert.Equal(["Today"], due);
        Assert.Single(page.FindAll(".roadmap-milestone-line.roadmap-milestone--due"));
    }

    [Fact]
    public void Every_milestone_has_a_chip_for_either_side_to_show_when_its_line_is_out_of_view()
    {
        // Beyond the window (60 days ahead) it has no line, but still a chip.
        _milestones = [Milestone("Beta", 3), Milestone("Launch", 90)];
        var page = RenderPage();

        Assert.Equal(["Beta"], Flags(page));
        Assert.Equal(2, page.FindAll(".roadmap-milestone-hints__before [data-roadmap-milestone-hint]").Count);
        var after = page.FindAll(".roadmap-milestone-hints__after [data-roadmap-milestone-hint]");
        Assert.Equal(["33", "120"], after.Select(c => c.GetAttribute("data-day")));
        Assert.Contains("Launch", after[1].TextContent);
    }

    [Fact]
    public async Task Right_clicking_a_day_adds_a_milestone_on_it_in_the_default_red()
    {
        var day = Today.AddDays(2);
        _http.Route($"roadmaps/{RoadmapId}/milestones", HttpStatusCode.OK,
            new RoadmapMilestoneDto(Guid.NewGuid(), "Beta", day, RoadmapLimits.DefaultMilestoneColor, 1), HttpMethod.Post);
        _menu.Next = "add-milestone";
        var dialogs = Render<MudBlazor.MudDialogProvider>();
        var page = RenderPage();

        var menu = page.InvokeAsync(() => page.Instance.OnDayMenu(32, 100, 200));
        dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindAll("input")));
        dialogs.Find("input").Input("Beta");
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Add").Click());
        await menu;

        page.WaitForAssertion(() => Assert.Equal(["Beta"], Flags(page)));
        var body = Body("POST", "/milestones");
        Assert.Contains("\"name\":\"Beta\"", body);
        Assert.Contains($"\"day\":\"{day:yyyy-MM-dd}\"", body);
        Assert.Contains($"\"color\":\"{RoadmapLimits.DefaultMilestoneColor}\"", body);
    }

    [Fact]
    public async Task Dropping_a_milestone_on_another_day_saves_it_over_the_version_it_had()
    {
        _milestones = [Milestone("Beta", 3)];
        var moved = _milestones[0] with { Day = Today.AddDays(5), Version = 8 };
        _http.Route($"milestones/{moved.Id}", HttpStatusCode.OK, moved, HttpMethod.Put);
        var page = RenderPage();

        await page.Instance.OnMilestoneMove(moved.Id.ToString(), 35);

        page.WaitForAssertion(() => Assert.Contains("--milestone-x:1440px",
            page.Find(".roadmap-milestone-line").GetAttribute("style")));
        var body = Assert.Single(Requests("PUT", "/milestones/"));
        Assert.Contains($"\"day\":\"{Today.AddDays(5):yyyy-MM-dd}\"", body);
        Assert.Contains("\"name\":\"Beta\"", body);
        Assert.Contains("\"version\":7", body);
    }

    [Fact]
    public async Task A_failed_milestone_move_puts_it_back()
    {
        _milestones = [Milestone("Beta", 3)];
        _http.Route($"milestones/{_milestones[0].Id}", HttpStatusCode.InternalServerError, (object?)null, HttpMethod.Put);
        var page = RenderPage();

        await page.Instance.OnMilestoneMove(_milestones[0].Id.ToString(), 40);

        page.WaitForAssertion(() => Assert.Contains("--milestone-x:1360px",
            page.Find(".roadmap-milestone-line").GetAttribute("style")));
    }

    [Fact]
    public async Task Clicking_a_flag_opens_its_dialog_where_it_can_be_deleted()
    {
        _milestones = [Milestone("Beta", 3)];
        var id = _milestones[0].Id;
        _http.Route($"milestones/{id}", HttpStatusCode.NoContent, (object?)null, HttpMethod.Delete);
        var dialogs = Render<MudBlazor.MudDialogProvider>();
        var page = RenderPage();

        var click = page.InvokeAsync(() => page.Instance.OnMilestoneClick(id.ToString()));
        dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindAll("input")));
        Assert.Equal("Beta", dialogs.Find("input").GetAttribute("value"));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Delete").Click());
        await click;

        page.WaitForAssertion(() => Assert.Empty(Flags(page)));
        Assert.Contains($"DELETE /api/ext/roadmap/projects/{ProjectId}/roadmaps/{RoadmapId}/milestones/{id}", _http.Paths);
    }

    [Fact]
    public async Task A_milestones_menu_edits_or_deletes_it()
    {
        _milestones = [Milestone("Beta", 3)];
        _http.Route($"milestones/{_milestones[0].Id}", HttpStatusCode.NoContent, (object?)null, HttpMethod.Delete);
        _menu.Next = "remove";
        var page = RenderPage();

        await page.Instance.OnMilestoneMenu(_milestones[0].Id.ToString(), 10, 10);

        Assert.Equal(["Edit milestone", "Delete milestone"], _menu.Labels());
        Assert.True(_menu.Shown[0][1].Danger);
        page.WaitForAssertion(() => Assert.Empty(Flags(page)));
    }

    [Fact]
    public async Task A_read_only_member_sees_milestones_but_cannot_change_them()
    {
        _role = ProjectRole.Read;
        _milestones = [Milestone("Beta", 3)];
        var dialogs = Render<MudBlazor.MudDialogProvider>();
        var page = RenderPage();

        await page.Instance.OnMilestoneClick(_milestones[0].Id.ToString());
        await page.Instance.OnMilestoneMenu(_milestones[0].Id.ToString(), 10, 10);
        await page.Instance.OnMilestoneMove(_milestones[0].Id.ToString(), 40);

        Assert.Equal(["Beta"], Flags(page));
        Assert.Empty(dialogs.FindAll("input"));
        Assert.Empty(_menu.Shown);
        Assert.DoesNotContain(_http.Paths, p => p.StartsWith("PUT "));
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Add milestone"));
        Assert.DoesNotContain("roadmap-milestone--movable", page.Markup);
    }

    private static AngleSharp.Dom.IElement AddTaskButton(IRenderedComponent<RoadmapPage> page) =>
        page.FindAll("button").Single(b => b.TextContent.Contains("Add task"));

    private static List<string> Selected(IRenderedComponent<RoadmapPage> page) =>
        page.FindAll(".roadmap-bar--selected .roadmap-bar__text:not(.roadmap-bar__text--copy) .roadmap-bar__name")
            .Select(n => n.TextContent.Trim())
            .ToList();

    private string Body(string method, string pathEnd) =>
        _http.Bodies[_http.Paths.FindIndex(p => p.StartsWith($"{method} ") && p.EndsWith(pathEnd))];

    /// <summary>Stands in for the host's context menu: records each one shown and answers <see cref="Next"/>.</summary>
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

    /// <summary>Stands in for the host's picker: records what it was asked and hands back <see cref="Next"/>.</summary>
    private sealed class FakePicker : ISoloTaskPicker
    {
        public TaskDto? Next { get; set; }

        public Action? During { get; set; }

        public List<(Guid ProjectId, TaskPickerOptions? Options)> Calls { get; } = [];

        public Task<TaskDto?> PickAsync(Guid projectId, TaskPickerOptions? options = null, CancellationToken ct = default)
        {
            Calls.Add((projectId, options));
            During?.Invoke();
            return Task.FromResult(Next);
        }
    }

    private sealed class IdleDrawer : ISoloTaskDrawer
    {
        public Task OpenAsync(Guid taskId, CancellationToken ct = default) => Task.CompletedTask;

        public event Action<TaskDrawerChange>? Changed
        {
            add { }
            remove { }
        }

        public event Action? Closed
        {
            add { }
            remove { }
        }
    }
}
