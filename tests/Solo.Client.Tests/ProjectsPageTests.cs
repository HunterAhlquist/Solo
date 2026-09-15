using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The project list doubles as a board index: each card carries its project's
/// open boards, so the whole instance is one click deep from the landing page.
/// </summary>
public class ProjectsPageTests : ComponentTestBase
{
    private readonly StubHttp _http = new();

    public ProjectsPageTests()
    {
        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new UserSession(api));
        Services.AddSingleton(new ProjectNavStore(api));
    }


    /// <summary>
    /// The boards route has to be offered first: its path contains "/api/projects"
    /// too, and the stub answers with the first fragment that matches.
    /// </summary>
    private void Route(params BoardDto[] boards)
    {
        _http.RouteJson("/api/users/me", new UserDto(TestData.MeId, "h@x.io", "Hunter", null, false));
        _http.RouteJson("/boards", boards);
        _http.RouteJson("/api/projects",
            new[]
            {
                new ProjectSummaryDto(TestData.ProjectId, "Solo", "SOLO", null, null,
                    ProjectRole.ReadWrite, IsOwner: true),
            });
    }

    private static BoardDto Board(string name, string alias, bool isOpen = true) =>
        new(Guid.NewGuid(), TestData.ProjectId, name, alias, null, null, isOpen, 1);

    [Fact]
    public void Each_board_is_a_link_to_that_board()
    {
        Route(Board("Gameplay", "GAME"));

        var page = Render<Pages.Projects>();

        var link = page.Find(".project-card__board");
        Assert.Contains("Gameplay", link.TextContent);
        Assert.Equal("projects/SOLO/boards/GAME", link.GetAttribute("href"));
    }

    [Fact]
    public void A_closed_board_stays_off_the_card()
    {
        // Same rule as the sidebar: closed boards live in the project home's
        // faded foldout, not in a listing meant to show live work.
        Route(Board("Gameplay", "GAME"), Board("Alpha", "ALPHA", isOpen: false));

        var page = Render<Pages.Projects>();

        Assert.Single(page.FindAll(".project-card__board"));
        Assert.DoesNotContain("Alpha", page.Markup);
    }

    [Fact]
    public void A_project_with_no_open_boards_says_so()
    {
        Route();

        var page = Render<Pages.Projects>();

        Assert.Empty(page.FindAll(".project-card__board"));
        Assert.Contains("No open boards.", page.Markup);
    }

    /// <summary>
    /// The card is not itself a link: its rows and its settings button are the
    /// only things that navigate, so a click on the card body goes nowhere.
    /// bUnit throws when a clicked element has no handler above it either, which
    /// is exactly the absence being asserted.
    /// </summary>
    [Fact]
    public void The_card_body_is_not_one_big_link()
    {
        Route(Board("Gameplay", "GAME"));

        var page = Render<Pages.Projects>();

        Assert.Throws<MissingEventHandlerException>(() => page.Find(".mud-card").Click());
    }

    [Fact]
    public void The_card_closes_with_a_settings_button_onto_the_project_home()
    {
        Route(Board("Gameplay", "GAME"));

        var page = Render<Pages.Projects>();
        page.Find(".mud-card-actions button").Click();

        Assert.EndsWith("projects/SOLO", Services.GetRequiredService<NavigationManager>().Uri);
    }
}
