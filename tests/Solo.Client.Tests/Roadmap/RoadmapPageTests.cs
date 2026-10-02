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
    private List<RoadmapEntryDto> _entries = [Entry("Save system", 1)];

    public RoadmapPageTests()
    {
        var client = _http.CreateClient();
        Services.AddSingleton<ISoloClientApi>(client);
        Services.AddSingleton<ISoloLiveTopics>(_live);
        Services.AddSingleton<ISoloTaskDrawer>(new IdleDrawer());
        JSInterop.SetupModule(new RoadmapApi(client).Content("roadmap.js").ToString());

        _http.RouteJson("/members", new[]
        {
            new ProjectMemberDto(Guid.NewGuid(), TestData.OtherId, "riley@example.com", "Riley Park", null,
                ProjectRole.ReadWrite, false, false),
        });
        _http.RouteJson("/labels", Array.Empty<LabelDto>());
        _http.Route($"roadmaps/{RoadmapId}", HttpStatusCode.OK, () => Timeline(_entries));
        _http.RouteJson("/api/projects", new[]
        {
            new ProjectSummaryDto(ProjectId, "Solo", "SOLO", null, null, ProjectRole.ReadWrite, false),
        });
    }

    private static RoadmapEntryDto Entry(string name, int number) =>
        new(Guid.NewGuid(), number * 1024m, DateOnly.FromDateTime(DateTime.Today), "FEAT", "Features",
            TestData.Task(number: number, deadline: DateOnly.FromDateTime(DateTime.Today).AddDays(3)) with { Name = name });

    private static RoadmapTimelineDto Timeline(IEnumerable<RoadmapEntryDto> entries) =>
        new(RoadmapId, ProjectId, "SOLO", "2027 Launch", 1,
            new RoadmapSettingsDto(30, 60, DayOfWeek.Monday, 2, 1), entries.ToList());

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
