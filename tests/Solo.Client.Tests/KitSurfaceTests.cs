using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Api;
using Solo.Client.Extensions;
using Solo.Client.Kit;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Kit;

namespace Solo.Client.Tests;

/// <summary>
/// The kit on a kit surface: nothing is drawn, and each component is written
/// out as a node saying what it is, for an app to draw with controls of its
/// own. A press on a node is a click on it.
/// </summary>
public class KitSurfaceTests : ComponentTestBase
{
    public KitSurfaceTests() => Services.AddSingleton<ISoloKitRenderer, MudKitRenderer>();

    /// <summary>Renders kit markup as the surface page does: under a cascaded <see cref="KitSurface"/>.</summary>
    private IRenderedComponent<CascadingValue<KitSurface>> OnSurface(RenderFragment content) =>
        Render<CascadingValue<KitSurface>>(parameters => parameters
            .Add(c => c.Value, new KitSurface())
            .Add(c => c.ChildContent, content));

    private sealed class Switchboard : ComponentBase
    {
        private bool _on;
        private int _count;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<SoloToggle>(0);
            builder.AddComponentParameter(1, nameof(SoloToggle.Label), _on ? "Switched on" : "Switched off");
            builder.AddComponentParameter(2, nameof(SoloToggle.Value), _on);
            builder.AddComponentParameter(3, nameof(SoloToggle.ValueChanged), EventCallback.Factory.Create<bool>(this, value => _on = value));
            builder.CloseComponent();
            builder.OpenComponent<SoloButton>(4);
            builder.AddComponentParameter(5, nameof(SoloButton.Text), $"Pressed {_count}");
            builder.AddComponentParameter(6, nameof(SoloButton.OnClick), EventCallback.Factory.Create(this, () => _count++));
            builder.CloseComponent();
        }
    }

    [Fact]
    public void A_kit_component_becomes_a_node_and_not_markup()
    {
        var surface = OnSurface(builder =>
        {
            builder.OpenComponent<SoloBadge>(0);
            builder.AddComponentParameter(1, nameof(SoloBadge.Text), "3 left");
            builder.AddComponentParameter(2, nameof(SoloBadge.Tone), SoloTone.Warning);
            builder.AddComponentParameter(3, nameof(SoloBadge.Icon), "Flag");
            builder.CloseComponent();
        });

        var node = surface.Find("solo-kit");
        Assert.Equal("badge", node.GetAttribute("data-kind"));
        Assert.Equal("3 left", node.GetAttribute("data-text"));
        Assert.Equal("Warning", node.GetAttribute("data-tone"));
        Assert.Equal("Flag", node.GetAttribute("data-icon"));
        Assert.Empty(surface.FindAll(".mud-chip"));
    }

    [Fact]
    public void A_node_carries_only_what_it_was_given()
    {
        var surface = OnSurface(builder =>
        {
            builder.OpenComponent<SoloRow>(0);
            builder.AddComponentParameter(1, nameof(SoloRow.Title), "Roadmap");
            builder.AddComponentParameter(2, nameof(SoloRow.Href), "projects/DMD/roadmaps/1");
            builder.CloseComponent();
        });

        var row = surface.Find("solo-kit[data-kind=row]");
        Assert.Equal("projects/DMD/roadmaps/1", row.GetAttribute("data-href"));
        // No handler, not disabled, no subtitle: none of those is said at all.
        Assert.False(row.HasAttribute("data-press"));
        Assert.False(row.HasAttribute("data-disabled"));
        Assert.False(row.HasAttribute("data-subtitle"));
    }

    [Fact]
    public void Children_nest_inside_the_node_that_holds_them()
    {
        var surface = OnSurface(builder =>
        {
            builder.OpenComponent<SoloRow>(0);
            builder.AddComponentParameter(1, nameof(SoloRow.Title), "Sprint 4");
            builder.AddComponentParameter(2, nameof(SoloRow.ChildContent), (RenderFragment)(child =>
            {
                child.OpenComponent<SoloBadge>(0);
                child.AddComponentParameter(1, nameof(SoloBadge.Text), "3 left");
                child.CloseComponent();
            }));
            builder.CloseComponent();
        });

        Assert.Equal("3 left", surface.Find("solo-kit[data-kind=row] > solo-kit[data-kind=badge]").GetAttribute("data-text"));
    }

    [Fact]
    public void A_press_on_a_button_reaches_the_extension_and_what_it_shows_follows()
    {
        var surface = OnSurface(builder =>
        {
            builder.OpenComponent<Switchboard>(0);
            builder.CloseComponent();
        });

        var button = surface.Find("solo-kit[data-kind=button]");
        Assert.Equal("1", button.GetAttribute("data-press"));
        button.Click();

        Assert.Equal("Pressed 1", surface.Find("solo-kit[data-kind=button]").GetAttribute("data-text"));
    }

    [Fact]
    public void A_press_on_a_switch_asks_for_the_other_value()
    {
        var surface = OnSurface(builder =>
        {
            builder.OpenComponent<Switchboard>(0);
            builder.CloseComponent();
        });

        var toggle = surface.Find("solo-kit[data-kind=toggle]");
        Assert.False(toggle.HasAttribute("data-value"));
        toggle.Click();

        toggle = surface.Find("solo-kit[data-kind=toggle]");
        Assert.Equal("1", toggle.GetAttribute("data-value"));
        Assert.Equal("Switched on", toggle.GetAttribute("data-label"));

        toggle.Click();
        Assert.False(surface.Find("solo-kit[data-kind=toggle]").HasAttribute("data-value"));
    }

    [Fact]
    public void Text_is_carried_as_text()
    {
        var surface = OnSurface(builder =>
        {
            builder.OpenComponent<SoloText>(0);
            builder.AddComponentParameter(1, nameof(SoloText.Text), "<b>bold</b> & \"quoted\"");
            builder.CloseComponent();
        });

        Assert.Equal("<b>bold</b> & \"quoted\"", surface.Find("solo-kit").GetAttribute("data-text"));
        Assert.Empty(surface.FindAll("b"));
    }

    /// <summary>This test assembly stands in for an extension's client half, as in the other section tests.</summary>
    private static ExtensionCatalog Catalog() => new()
    {
        Loaded =
        [
            new LoadedExtension(
                new ClientExtensionDto("fixture", "Fixture", "1.0.0", ">=1.17 <2.0", "Fixture", []),
                Assembly.GetExecutingAssembly()),
        ],
    };

    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    [Fact]
    public void A_seam_s_contributions_are_core_s_in_core_s_order_and_say_whose_they_are()
    {
        var home = KitSeams.Find(KitSeams.ProjectHome, Catalog(), Nothing);

        Assert.Equal(["Plans", "Broken"], home.Select(c => c.Title));
        Assert.All(home, contribution => Assert.Equal("fixture", contribution.ExtensionId));
        Assert.Equal(typeof(PlansHomeSection), home[0].Component);
    }

    [Fact]
    public void An_extension_the_app_draws_itself_is_left_out()
    {
        Assert.NotEmpty(KitSeams.Find(KitSeams.ProjectCard, Catalog(), Nothing));
        Assert.Empty(KitSeams.Find(KitSeams.ProjectCard, Catalog(), new HashSet<string> { "fixture" }));
        Assert.Empty(KitSeams.Find("somewhere-else", Catalog(), Nothing));
    }

    private IRenderedComponent<KitMountView> Mounted(string seam, ProjectSummaryDto project)
    {
        var api = new StubHttp().RouteJson("api/projects", new List<ProjectSummaryDto> { project }).CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(Catalog());
        return Render<KitMountView>(parameters => parameters
            .Add(m => m.Mount, new KitMount("m1", seam, project.Id, null, null))
            .Add(m => m.Skip, Nothing));
    }

    [Fact]
    public void A_mounted_place_hands_each_contribution_what_core_hands_it_there()
    {
        var project = new ProjectSummaryDto(Guid.NewGuid(), "Demo", "DMD", null, null, ProjectRole.ReadWrite, false);

        var mount = Mounted(KitSeams.ProjectCard, project);

        mount.WaitForAssertion(() =>
        {
            var section = mount.Find("[data-kit-section][data-extension=fixture]");
            Assert.Equal("Plans", section.GetAttribute("data-title"));
            // The contribution ran with the project and what its viewer may do, as on a core page.
            Assert.Equal("DMD:True", section.TextContent.Trim());
        });
    }

    [Fact]
    public void A_contribution_that_throws_says_so_in_its_own_place_and_the_rest_stand()
    {
        var project = new ProjectSummaryDto(Guid.NewGuid(), FaultyHomeSection.Trigger, "FLT", null, null, ProjectRole.Read, false);

        var mount = Mounted(KitSeams.ProjectHome, project);

        mount.WaitForAssertion(() =>
        {
            var broken = mount.Find("[data-kit-section][data-title=Broken] solo-kit");
            Assert.Equal("text", broken.GetAttribute("data-kind"));
            Assert.Equal("Danger", broken.GetAttribute("data-tone"));
            Assert.Equal("FLT:False", mount.Find("[data-kit-section][data-title=Plans]").TextContent.Trim());
        });
    }

    [Fact]
    public void A_place_nobody_can_read_has_no_contributions()
    {
        var project = new ProjectSummaryDto(Guid.NewGuid(), "Demo", "DMD", null, null, ProjectRole.Read, false);
        Services.AddSingleton(new StubHttp().RouteJson("api/projects", new List<ProjectSummaryDto>()).CreateClient());
        Services.AddSingleton(Catalog());

        var mount = Render<KitMountView>(parameters => parameters
            .Add(m => m.Mount, new KitMount("m1", KitSeams.ProjectCard, project.Id, null, null))
            .Add(m => m.Skip, Nothing));

        Assert.Empty(mount.FindAll("[data-kit-section]"));
    }

    private static readonly ProjectSummaryDto Demo =
        new(TestData.ProjectId, "Demo", "DMD", null, null, ProjectRole.ReadWrite, false);

    private static readonly BoardDto Sprint = new(TestData.BoardId, TestData.ProjectId, "Sprint", "SPR", null, null, true, 1);

    /// <summary>A place the surface reads over the API: the routes it may ask for, and the mount itself.</summary>
    private IRenderedComponent<KitMountView> Mounted(
        KitMount mount, StubHttp http, ExtensionCatalog? catalog = null, Action<Guid>? onTaskChanged = null)
    {
        Services.AddSingleton(http.CreateClient());
        Services.AddSingleton(catalog ?? Catalog());
        return Render<KitMountView>(parameters =>
        {
            parameters.Add(m => m.Mount, mount).Add(m => m.Skip, Nothing);
            if (onTaskChanged is not null)
            {
                parameters.Add(m => m.OnTaskChanged, onTaskChanged);
            }
        });
    }

    /// <summary>The board's routes go first: a project's boards are under the projects' own path.</summary>
    private static StubHttp BoardRoutes(params TaskDto[] tasks) => new StubHttp()
        .RouteJson($"api/projects/{TestData.ProjectId}/boards", new List<BoardDto> { Sprint })
        .RouteJson($"api/boards/{TestData.BoardId}/tasks", tasks.ToList())
        .RouteJson("api/projects", new List<ProjectSummaryDto> { Demo });

    [Fact]
    public void A_board_s_columns_are_an_item_apiece_each_handed_its_own_cards()
    {
        var http = BoardRoutes(
            TestData.Task(status: TaskState.Unstarted),
            TestData.Task(status: TaskState.Finished),
            TestData.Task(status: TaskState.Unstarted));

        var mount = Mounted(new KitMount("m1", KitSeams.BoardColumn, TestData.ProjectId, TestData.BoardId, null), http);

        mount.WaitForAssertion(() =>
        {
            Assert.Equal("Unstarted:2", mount.Find("[data-kit-item=Unstarted] .test-column").TextContent);
            Assert.Equal("InProgress:0", mount.Find("[data-kit-item=InProgress] .test-column").TextContent);
            Assert.Equal("Finished:1", mount.Find("[data-kit-item=Finished] .test-column").TextContent);
        });
    }

    [Fact]
    public void A_card_on_Your_Tasks_is_told_what_its_board_s_card_is_told()
    {
        var mine = TestData.Task();
        // A task whose board didn't come with it has no project to be told of, and is left out.
        var stray = TestData.Task() with { BoardId = Guid.NewGuid() };
        var http = new StubHttp().RouteJson(
            "api/users/me/tasks", new AssignedTasksDto([Demo], [Sprint], [], [], [mine, stray]));

        var mount = Mounted(new KitMount("m1", KitSeams.AssignedCards, null, null, null), http);

        mount.WaitForAssertion(() =>
        {
            Assert.Equal("Task:True:True", mount.Find($"[data-kit-item='{mine.Id}'] .test-card-badge").TextContent);
            Assert.Empty(mount.FindAll($"[data-kit-item='{stray.Id}']"));
        });
    }

    [Fact]
    public void Settings_sections_carry_core_s_title_and_what_is_being_set_up()
    {
        var project = Mounted(new KitMount("m1", KitSeams.ProjectSettings, TestData.ProjectId, null, null), BoardRoutes());

        project.WaitForAssertion(() => Assert.Equal(
            "DMD:True", project.Find("[data-kit-section][data-title=Automation] .test-project-settings").TextContent));

        var board = Render<KitMountView>(parameters => parameters
            .Add(m => m.Mount, new KitMount("m2", KitSeams.BoardSettings, TestData.ProjectId, TestData.BoardId, null))
            .Add(m => m.Skip, Nothing));

        board.WaitForAssertion(() => Assert.Equal(
            "SPR:True", board.Find("[data-kit-section][data-title=Limits] .test-board-settings").TextContent));
    }

    [Fact]
    public void An_extension_s_settings_panel_is_its_own_and_nobody_else_s()
    {
        var catalog = new ExtensionCatalog
        {
            Loaded =
            [
                .. Catalog().Loaded,
                new LoadedExtension(
                    new ClientExtensionDto("other", "Other", "1.0.0", ">=1.17 <2.0", "Other", []), Assembly.GetExecutingAssembly()),
            ],
        };

        var mount = Mounted(new KitMount("m1", KitSeams.ExtensionSettings, null, null, null, "other"), new StubHttp(), catalog);

        mount.WaitForAssertion(() =>
        {
            Assert.Equal("other", mount.Find(".test-settings-panel").TextContent);
            Assert.All(mount.FindAll("[data-kit-section]"), section => Assert.Equal("other", section.GetAttribute("data-extension")));
        });
    }

    [Fact]
    public void The_app_bar_needs_nothing_read_to_be_drawn()
    {
        var http = new StubHttp();

        var mount = Mounted(new KitMount("m1", KitSeams.AppBar, null, null, null), http);

        Assert.Single(mount.FindAll("[data-kit-section] .test-app-bar"));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void A_task_s_menu_is_what_each_contributor_answers_and_a_press_is_its_choice()
    {
        var task = TestData.Task();
        var log = new MenuLog();
        Services.AddSingleton(log);
        var http = new StubHttp()
            .RouteJson($"api/tasks/{task.Id}", task)
            .RouteJson("api/projects", new List<ProjectSummaryDto> { Demo });
        var changed = new List<Guid>();

        var mount = Mounted(new KitMount("m1", KitSeams.TaskMenu, TestData.ProjectId, null, task.Id), http, onTaskChanged: changed.Add);

        mount.WaitForAssertion(() =>
        {
            // The contributor that throws costs its own items and nothing else.
            var item = Assert.Single(mount.FindAll("solo-kit[data-kind=menuItem]"));
            Assert.Equal("Plan it", item.GetAttribute("data-label"));
            Assert.Equal("1", item.GetAttribute("data-press"));
            Assert.False(item.HasAttribute("data-danger"));
        });

        mount.Find("solo-kit[data-kind=menuItem]").Click();

        // Made from the host's services, told which task, and the app is told to read the task again.
        Assert.Equal([("open", task.Id, true)], log.Chosen);
        Assert.Equal([task.Id], changed);
    }

    [Fact]
    public void Menu_contributors_are_classes_found_in_core_s_order()
    {
        var menu = KitSeams.Find(KitSeams.TaskMenu, Catalog(), Nothing);

        Assert.Equal([typeof(TestMenuContributor), typeof(TestBrokenMenuContributor)], menu.Select(c => c.Component));
        Assert.Empty(KitSeams.Find(KitSeams.TaskMenu, Catalog(), new HashSet<string> { "fixture" }));
    }

    [Fact]
    public void Every_kit_component_has_a_node()
    {
        foreach (var type in typeof(SoloKitComponent).Assembly.GetTypes()
                     .Where(type => type.IsSubclassOf(typeof(SoloKitComponent)) && !type.IsAbstract))
        {
            var surface = OnSurface(builder =>
            {
                builder.OpenComponent(0, type);
                builder.CloseComponent();
            });

            Assert.True(surface.FindAll("solo-kit[data-kind]").Count == 1, $"{type.Name} has no node on a kit surface");
        }
    }
}
