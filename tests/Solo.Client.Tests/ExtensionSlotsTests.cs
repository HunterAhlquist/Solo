using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Api;
using Solo.Client.Auth;
using Solo.Client.Components;
using Solo.Client.Extensions;
using Solo.Client.Layout;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// Contract 1.8's places to contribute: task cards, board column headers, the
/// task menu, project and board settings, and the app bar. This test assembly
/// stands in for an extension's client half, as in the other section tests.
/// </summary>
public class ExtensionSlotsTests : ComponentTestBase
{
    private readonly StubHttp _http = new();
    private readonly FakeBoardHub _hub = new();
    private readonly BoardStore _store;
    private readonly MenuLog _menuLog = new();

    public ExtensionSlotsTests()
    {
        JSInterop.SetupModule("./js/finished-column.js");
        Services.AddMudMarkdownServices();

        var api = _http.CreateClient();
        var session = new UserSession(api);
        _store = new BoardStore(api, _hub, session);

        Services.AddSingleton(api);
        Services.AddSingleton(session);
        Services.AddSingleton(_store);
        Services.AddSingleton<IBoardHubClient>(_hub);
        Services.AddSingleton(new ProjectNavStore(api));
        Services.AddScoped<RecentProjectsStore>();
        Services.AddScoped<ProjectOrderStore>();
        Services.AddScoped<DragBridge>();
        Services.AddSingleton(_menuLog);
    }

    private static readonly ProjectMemberDto Me = TestData.Member(TestData.MeId, "Hunter") with
    {
        IsOwner = true,
        Role = ProjectRole.ReadWrite,
    };

    private static ExtensionCatalog Catalog() => new()
    {
        Loaded =
        [
            new LoadedExtension(
                new ClientExtensionDto("fixture", "Fixture", "1.0.0", ">=1.8 <2.0", "Fixture", []),
                Assembly.GetExecutingAssembly()),
        ],
    };

    private void WithExtensions() => Services.AddSingleton(Catalog());

    private ContextMenuService Menu => Services.GetRequiredService<ContextMenuService>();

    // -- The board --------------------------------------------------------------

    private static readonly TaskDto Unstarted = TestData.Task(status: TaskState.Unstarted) with { Name = "Rig the boss" };

    private static readonly TaskDto Finished = TestData.Task(id: Guid.NewGuid(), status: TaskState.Finished) with
    {
        Name = "Ship the demo",
        Number = 2,
    };

    private IRenderedComponent<Pages.Board> RenderBoard()
    {
        _http.RespondJson(new UserDto(TestData.MeId, "me@example.com", "Me", null, false));
        _http.RespondJson(TestData.Board(tasks: [Unstarted, Finished]));
        _http.RespondJson(new[] { Me });
        return Render<Pages.Board>(p => p.Add(b => b.ProjectAlias, "PROJ").Add(b => b.BoardAlias, "BRD"));
    }

    [Fact]
    public void Every_card_carries_the_extensions_contribution_with_its_task()
    {
        WithExtensions();

        var page = RenderBoard();

        page.WaitForAssertion(() => Assert.Equal(
            ["Rig the boss:True:True", "Ship the demo:True:True"],
            page.FindAll(".test-card-badge").Select(b => b.TextContent).Order()));
    }

    [Fact]
    public void A_column_header_contribution_gets_that_columns_tasks()
    {
        WithExtensions();

        var page = RenderBoard();

        page.WaitForAssertion(() => Assert.Equal(
            ["Unstarted:1", "InProgress:0", "Finished:1"],
            page.FindAll(".test-column").Select(c => c.TextContent)));
    }

    [Fact]
    public void Without_contributions_the_board_draws_none_and_leaves_right_click_alone()
    {
        var page = RenderBoard();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".kanban-card-slot")));

        Assert.Empty(page.FindAll(".test-card-badge, .test-column, .kanban-card__extensions"));
        page.FindAll(".kanban-card-slot")[0].ContextMenu(new MouseEventArgs { ClientX = 10, ClientY = 20 });
        Assert.Null(Menu.Current);
    }

    [Fact]
    public async Task Right_clicking_a_card_offers_core_then_each_extensions_items()
    {
        WithExtensions();
        var page = RenderBoard();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".kanban-card-slot")));

        var card = page.FindAll(".kanban-card-slot").First(c => c.TextContent.Contains("Rig the boss"));
        // Not awaited: the handler waits for the person's choice, made below.
        var shown = card.ContextMenuAsync(new MouseEventArgs { ClientX = 10, ClientY = 20 });

        // The broken contributor costs only its own items; the menu still opens.
        page.WaitForAssertion(() => Assert.Equal(["Open task", "Plan it"], Menu.Current!.Items.Select(i => i.Label)));
        Assert.True(Menu.Current!.Items[1].SeparatorBefore);
        // Both said "open"; the host kept them apart and hands back the extension's own id.
        Menu.Choose(Menu.Current.Items[1].Id);

        await shown;
        Assert.Equal([("open", Unstarted.Id, true)], _menuLog.Chosen);
    }

    [Fact]
    public async Task Choosing_core_open_opens_the_task()
    {
        WithExtensions();
        var page = RenderBoard();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".kanban-card-slot")));

        var shown = page.FindAll(".kanban-card-slot")[0].ContextMenuAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Assert.NotNull(Menu.Current));
        Menu.Choose(Menu.Current!.Items[0].Id);
        await shown;

        page.WaitForAssertion(() =>
            Assert.Contains(_hub.PresenceReports, r => r.EntityType == "task" && r.Mode == PresenceMode.Viewing));
        Assert.Empty(_menuLog.Chosen);
    }

    [Fact]
    public async Task The_drawer_reaches_the_same_items_without_a_right_click()
    {
        WithExtensions();
        var page = RenderBoard();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".kanban-card")));

        page.FindAll(".kanban-card").First(c => c.TextContent.Contains("Rig the boss")).Click();
        page.WaitForAssertion(() => page.Find("[aria-label='More actions']"));
        var shown = page.Find("[aria-label='More actions']").ClickAsync(new MouseEventArgs());

        // No core items here: the drawer already is the open task.
        page.WaitForAssertion(() => Assert.Equal(["Plan it"], Menu.Current!.Items.Select(i => i.Label)));
        Menu.Dismiss();
        await shown;
    }

    [Fact]
    public void The_drawer_has_no_menu_button_when_nothing_contributes()
    {
        var page = RenderBoard();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".kanban-card")));

        page.FindAll(".kanban-card")[0].Click();

        page.WaitForAssertion(() => page.Find("[aria-label='Close']"));
        Assert.Empty(page.FindAll("[aria-label='More actions']"));
    }

    // -- Settings -----------------------------------------------------------------

    [Fact]
    public void A_project_settings_section_sits_in_the_Settings_tab_above_the_danger_zone()
    {
        WithExtensions();
        _http.RouteJson("/api/users/me", new UserDto(TestData.MeId, "h@x.io", "Hunter", null, false));
        _http.RouteJson("/members", new[] { Me });
        _http.RouteJson("/boards", Array.Empty<BoardDto>());
        _http.RouteJson("/labels", Array.Empty<LabelDto>());
        _http.RouteJson("/api/projects/by-alias/SOLO",
            new ProjectDto(TestData.ProjectId, "Solo", "SOLO", null, null, 1, [Me]));

        var page = Render<Pages.ProjectHome>(p => p.Add(x => x.ProjectAlias, "SOLO"));
        page.WaitForAssertion(() => page.FindAll(".mud-tab").First(t => t.TextContent.Contains("Settings")).Click());

        page.WaitForAssertion(() => Assert.Equal("SOLO:True", page.Find(".test-project-settings").TextContent));
        var markup = page.Markup;
        Assert.True(markup.IndexOf("Automation", StringComparison.Ordinal)
                    < markup.IndexOf("Danger zone", StringComparison.Ordinal));
    }

    [Fact]
    public void A_board_settings_section_shows_in_the_board_settings_dialog()
    {
        WithExtensions();
        _http.RouteJson("/labels", Array.Empty<LabelDto>());
        _http.RouteJson("/boards", new[]
        {
            new BoardDto(TestData.BoardId, TestData.ProjectId, "Gameplay", "BRD", null, null, true, 1),
        });
        var dialogs = Render<MudDialogProvider>();
        var grid = Render<BoardsGrid>(p => p
            .Add(g => g.ProjectId, TestData.ProjectId)
            .Add(g => g.ProjectAlias, "PROJ")
            .Add(g => g.CanWrite, true));

        grid.WaitForAssertion(() => grid.FindAll("button").First(b => b.TextContent.Contains("Board settings")).Click());

        dialogs.WaitForAssertion(() => Assert.Equal("BRD:True", dialogs.Find(".test-board-settings").TextContent));
        Assert.Contains("Limits", dialogs.Markup);
    }

    // -- The app bar ----------------------------------------------------------------

    [Fact]
    public void An_app_bar_contribution_renders_once_signed_in()
    {
        WithExtensions();
        this.AddAuthorization().SetAuthorized("Hunter");
        Services.AddSingleton(new AppConfig());
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();
        Services.AddScoped<TaskDrawerService>();
        _http.RouteJson("/api/projects", Array.Empty<ProjectSummaryDto>());

        var layout = Render<MainLayout>();

        layout.WaitForAssertion(() => Assert.Single(layout.FindAll(".mud-appbar .test-app-bar")));
    }
}

/// <summary>What the test menu contributor was asked to do — injected, to show contributors get the host's services.</summary>
public sealed class MenuLog
{
    public List<(string ItemId, Guid TaskId, bool CanWrite)> Chosen { get; } = [];
}

[TaskCardExtension]
public sealed class TestCardBadge : ComponentBase
{
    [Parameter] public TaskDto Task { get; set; } = null!;
    [Parameter] public Guid ProjectId { get; set; }
    [Parameter] public bool CanWrite { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", "test-card-badge");
        builder.AddContent(2, $"{Task.Name}:{ProjectId == TestData.ProjectId}:{CanWrite}");
        builder.CloseElement();
    }
}

[BoardColumnExtension]
public sealed class TestColumnCount : ComponentBase
{
    [Parameter] public BoardDto Board { get; set; } = null!;
    [Parameter] public TaskState Status { get; set; }
    [Parameter] public IReadOnlyList<TaskDto> Tasks { get; set; } = [];
    [Parameter] public bool CanWrite { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", "test-column");
        builder.AddContent(2, $"{Status}:{Tasks.Count}");
        builder.CloseElement();
    }
}

[AppBarExtension]
public sealed class TestAppBarButton : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", "test-app-bar");
        builder.CloseElement();
    }
}

[ProjectSettingsSection("Automation")]
public sealed class TestProjectSettings : ComponentBase
{
    [Parameter] public ProjectSummaryDto Project { get; set; } = null!;
    [Parameter] public bool CanWrite { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "test-project-settings");
        builder.AddContent(2, $"{Project.Alias}:{CanWrite}");
        builder.CloseElement();
    }
}

[BoardSettingsSection("Limits")]
public sealed class TestBoardSettings : ComponentBase
{
    [Parameter] public BoardDto Board { get; set; } = null!;
    [Parameter] public bool CanWrite { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "test-board-settings");
        builder.AddContent(2, $"{Board.Alias}:{CanWrite}");
        builder.CloseElement();
    }
}

/// <summary>Says "open" on purpose: core's item has the same id.</summary>
[TaskMenuContributor]
public sealed class TestMenuContributor(MenuLog log) : ISoloTaskMenuContributor
{
    public ValueTask<IReadOnlyList<ContextMenuItem>> ItemsAsync(TaskMenuContext context, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ContextMenuItem>>([new ContextMenuItem("open", "Plan it")]);

    public Task OnChosenAsync(string itemId, TaskMenuContext context)
    {
        log.Chosen.Add((itemId, context.Task.Id, context.CanWrite));
        return Task.CompletedTask;
    }
}

[TaskMenuContributor(Order = 1)]
public sealed class TestBrokenMenuContributor : ISoloTaskMenuContributor
{
    public ValueTask<IReadOnlyList<ContextMenuItem>> ItemsAsync(TaskMenuContext context, CancellationToken ct) =>
        throw new InvalidOperationException("Broken on purpose.");

    public Task OnChosenAsync(string itemId, TaskMenuContext context) => Task.CompletedTask;
}
