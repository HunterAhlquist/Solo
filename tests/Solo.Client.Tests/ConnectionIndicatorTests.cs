using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Components;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Client.Theming;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Client.Tests;

/// <summary>The reconnecting hint only speaks up on a board whose feed is down.</summary>
public class ConnectionIndicatorTests : ComponentTestBase
{
    private IRenderedComponent<ConnectionIndicator> RenderIndicator(bool active, bool connected)
    {
        Services.AddSingleton<IBoardHubClient>(new FakeBoardHub { IsActive = active, IsConnected = connected });
        return Render<ConnectionIndicator>();
    }

    [Fact]
    public void Off_a_board_it_stays_silent_even_when_not_connected()
    {
        // The bug: on the project list (no board joined) the "disconnected" icon showed.
        var cut = RenderIndicator(active: false, connected: false);

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void On_a_board_with_a_live_connection_it_stays_silent()
    {
        var cut = RenderIndicator(active: true, connected: true);

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void On_a_board_whose_connection_dropped_it_warns()
    {
        var cut = RenderIndicator(active: true, connected: false);

        Assert.NotEmpty(cut.Markup.Trim());
    }
}
