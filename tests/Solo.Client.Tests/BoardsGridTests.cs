using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Solo.Client.Components;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The project's board listing is where a board is administered from — including
/// its labels, which belong to the board rather than to the project's defaults.
/// </summary>
public class BoardsGridTests : ComponentTestBase
{
    private readonly StubHttp _http = new();

    public BoardsGridTests()
    {
        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new ProjectNavStore(api));
    }

    private static BoardDto Board(bool isOpen = true) =>
        new(TestData.BoardId, TestData.ProjectId, "Gameplay", "BRD", null, null, isOpen, 1);

    /// <summary>
    /// Order matters: the board-labels path contains "/boards" too, so the
    /// narrower fragment has to be offered first.
    /// </summary>
    private void RouteOneBoard(bool isOpen = true)
    {
        _http.RouteJson("/labels", Array.Empty<LabelDto>());
        _http.RouteJson("/boards", new[] { Board(isOpen) });
    }

    private IRenderedComponent<BoardsGrid> RenderGrid(bool canWrite = true) =>
        Render<BoardsGrid>(p => p
            .Add(g => g.ProjectId, TestData.ProjectId)
            .Add(g => g.ProjectAlias, "PROJ")
            .Add(g => g.CanWrite, canWrite));

    private static List<IElement> LabelButtons(IRenderedComponent<BoardsGrid> grid) =>
        grid.FindAll("button").Where(b => b.TextContent.Contains("Labels")).ToList();

    [Fact]
    public void An_editor_gets_a_labels_button_on_each_open_board()
    {
        RouteOneBoard();

        var grid = RenderGrid();

        Assert.Single(LabelButtons(grid));
    }

    [Fact]
    public void A_read_only_member_gets_none()
    {
        RouteOneBoard();

        var grid = RenderGrid(canWrite: false);

        Assert.Empty(LabelButtons(grid));
    }

    [Fact]
    public void A_closed_board_is_administered_by_reopening_it_first()
    {
        RouteOneBoard(isOpen: false);

        var grid = RenderGrid();

        Assert.Empty(LabelButtons(grid));
        Assert.Contains("Reopen", grid.Markup);
    }

    [Fact]
    public void The_button_opens_that_boards_own_labels()
    {
        RouteOneBoard();

        var host = Render<MudDialogProvider>();
        var grid = RenderGrid();
        LabelButtons(grid).Single().Click();

        // The board-scoped copy of the tab, not the project's defaults.
        host.WaitForAssertion(() =>
            Assert.Contains("Only this board uses these labels.", host.Markup));
    }
}
