using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.Client.Components;
using Solo.Client.State;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// The host's context menu, opened through <see cref="ISoloContextMenu"/> the way
/// an extension page opens it: drawn by <see cref="ContextMenuHost"/> in a
/// browser, and handed to the desktop shell's native menu when there is one.
/// </summary>
public class ContextMenuTests : ComponentTestBase
{
    private static readonly ContextMenuItem[] Items =
    [
        new("open", "Open task") { Icon = Icons.Material.Filled.OpenInNew },
        new("remove", "Remove from roadmap") { Danger = true, SeparatorBefore = true },
    ];

    public ContextMenuTests() =>
        Services.AddScoped<ISoloContextMenu>(sp => sp.GetRequiredService<ContextMenuService>());

    private ISoloContextMenu Menu => Services.GetRequiredService<ISoloContextMenu>();

    private static List<string> Labels(IRenderedComponent<ContextMenuHost> host) =>
        host.FindAll("[role=menuitem]").Select(b => b.TextContent.Trim()).ToList();

    [Fact]
    public async Task The_menu_is_drawn_where_it_was_asked_for_and_returns_the_choice()
    {
        var host = Render<ContextMenuHost>();

        var shown = Menu.ShowAsync(120, 80, Items);

        host.WaitForAssertion(() => Assert.Equal(["Open task", "Remove from roadmap"], Labels(host)));
        Assert.Single(host.FindAll("[role=separator]"));
        Assert.Contains("solo-context-menu__item--danger", host.FindAll("[role=menuitem]")[1].ClassName);
        Assert.Contains("left:120px;top:80px", host.Find("[role=menu]").GetAttribute("style"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.contextMenu.place");

        host.FindAll("[role=menuitem]")[1].Click();

        Assert.Equal("remove", await shown);
        host.WaitForAssertion(() => Assert.Empty(host.FindAll("[role=menu]")));
    }

    [Fact]
    public async Task Escape_closes_it_with_nothing_chosen()
    {
        var host = Render<ContextMenuHost>();
        var shown = Menu.ShowAsync(0, 0, Items);
        host.WaitForAssertion(() => Assert.NotEmpty(Labels(host)));

        host.Find("[role=menu]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.Null(await shown);
        Assert.Empty(host.FindAll("[role=menu]"));
    }

    [Fact]
    public async Task A_click_outside_closes_it_with_nothing_chosen()
    {
        var host = Render<ContextMenuHost>();
        var shown = Menu.ShowAsync(0, 0, Items);
        host.WaitForAssertion(() => Assert.NotEmpty(Labels(host)));

        host.Find(".solo-context-menu-backdrop").Click();

        Assert.Null(await shown);
    }

    [Fact]
    public async Task A_second_menu_closes_the_first()
    {
        var host = Render<ContextMenuHost>();
        var first = Menu.ShowAsync(0, 0, Items);

        var second = Menu.ShowAsync(10, 10, [new ContextMenuItem("add", "Add a task on 7 Oct")]);

        Assert.Null(await first);
        host.WaitForAssertion(() => Assert.Equal(["Add a task on 7 Oct"], Labels(host)));
        host.Find("[role=menuitem]").Click();
        Assert.Equal("add", await second);
    }

    [Fact]
    public async Task A_disabled_item_is_shown_but_cant_be_chosen()
    {
        var host = Render<ContextMenuHost>();
        var shown = Menu.ShowAsync(0, 0, [Items[0] with { Disabled = true }, Items[1]]);
        host.WaitForAssertion(() => Assert.NotEmpty(Labels(host)));

        Assert.True(host.FindAll("[role=menuitem]")[0].HasAttribute("disabled"));
        Services.GetRequiredService<ContextMenuService>().Choose("open");

        Assert.False(shown.IsCompleted);
        host.FindAll("[role=menuitem]")[1].Click();
        Assert.Equal("remove", await shown);
    }

    [Fact]
    public async Task Navigating_away_closes_it()
    {
        var host = Render<ContextMenuHost>();
        var shown = Menu.ShowAsync(0, 0, Items);
        host.WaitForAssertion(() => Assert.NotEmpty(Labels(host)));

        Services.GetRequiredService<NavigationManager>().NavigateTo("projects/SOLO");

        Assert.Null(await shown);
        host.WaitForAssertion(() => Assert.Empty(host.FindAll("[role=menu]")));
    }

    [Fact]
    public async Task Cancelling_closes_it()
    {
        var host = Render<ContextMenuHost>();
        using var cts = new CancellationTokenSource();
        var shown = Menu.ShowAsync(0, 0, Items, cts.Token);
        host.WaitForAssertion(() => Assert.NotEmpty(Labels(host)));

        await cts.CancelAsync();

        Assert.Null(await shown);
        host.WaitForAssertion(() => Assert.Empty(host.FindAll("[role=menu]")));
    }

    [Fact]
    public async Task A_menu_with_nothing_on_it_isnt_shown()
    {
        var host = Render<ContextMenuHost>();

        Assert.Null(await Menu.ShowAsync(0, 0, []));
        Assert.Empty(host.FindAll("[role=menu]"));
    }

    [Fact]
    public async Task In_the_desktop_app_the_shell_shows_it_and_nothing_is_drawn()
    {
        var shell = new ShellJs("""{"id":"remove"}""");
        var service = new ContextMenuService(shell);

        Assert.Equal("remove", await service.ShowAsync(5, 5, Items));

        Assert.Null(service.Current);
        var (identifier, args) = Assert.Single(shell.Calls);
        Assert.Equal("solo.contextMenu.native", identifier);
        // Labels and ids only: a native menu has no icons or danger colour.
        Assert.Equal(
            """[[{"id":"open","label":"Open task","enabled":true,"separatorBefore":false},""" +
            """{"id":"remove","label":"Remove from roadmap","enabled":true,"separatorBefore":true}]]""",
            args);
    }

    [Fact]
    public async Task A_native_menu_closed_without_a_choice_returns_null()
    {
        var service = new ContextMenuService(new ShellJs("""{"id":null}"""));

        Assert.Null(await service.ShowAsync(5, 5, Items));
        Assert.Null(service.Current);
    }

    /// <summary>
    /// Plays the page's side of <c>solo.contextMenu.native</c> inside the shell:
    /// records the call as it would cross to JS, and answers with JSON.
    /// </summary>
    private sealed class ShellJs(string answer) : IJSRuntime
    {
        public List<(string Identifier, string Args)> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add((identifier, JsonSerializer.Serialize(args, JsonSerializerOptions.Web)));
            return ValueTask.FromResult(JsonSerializer.Deserialize<TValue>(answer, JsonSerializerOptions.Web)!);
        }
    }
}
