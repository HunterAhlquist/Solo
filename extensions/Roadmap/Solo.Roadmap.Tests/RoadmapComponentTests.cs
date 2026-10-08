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

namespace Solo.Roadmap.Tests;

/// <summary>The Roadmaps extension's client components, rendered against a stubbed API.</summary>
public class RoadmapComponentTests : ComponentTestBase
{
    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RoadmapId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private readonly StubHttp _http = new();
    private readonly FakeLiveTopics _live = new();

    public RoadmapComponentTests()
    {
        Services.AddSingleton<ISoloClientApi>(_http.CreateClient());
        Services.AddSingleton<ISoloLiveTopics>(_live);
    }

    [Fact]
    public void The_script_and_stylesheet_urls_carry_the_package_version()
    {
        var version = typeof(RoadmapApi).Assembly.GetName().Version!.ToString(3);
        var api = new RoadmapApi(_http.CreateClient());

        Assert.Equal($"https://solo.test/api/extensions/roadmap/content/roadmap.js?v={version}",
            api.Content("roadmap.js").ToString());
        Assert.EndsWith($"roadmap.css?v={version}", api.Content("roadmap.css").ToString());
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
