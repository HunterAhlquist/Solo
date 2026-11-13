using Microsoft.Extensions.DependencyInjection;
using Solo.Client.State;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// Device controls as an extension page sees them through
/// <see cref="ISoloDeviceActions"/>: the app shell's event reaches the newest
/// handler for its name, and nothing else.
/// </summary>
public class DeviceActionsTests : ComponentTestBase
{
    private DeviceActionsService Actions => (DeviceActionsService)Services.GetRequiredService<ISoloDeviceActions>();

    [Fact]
    public async Task The_newest_handler_takes_the_action_and_the_older_one_gets_it_back_when_it_goes()
    {
        var calls = new List<string>();
        using var page = Actions.Handle(DeviceActions.StylusToolSwap, () => { calls.Add("page"); return Task.CompletedTask; });
        var dialog = Actions.Handle(DeviceActions.StylusToolSwap, () => { calls.Add("dialog"); return Task.CompletedTask; });

        await Actions.Dispatch(DeviceActions.StylusToolSwap);
        dialog.Dispose();
        await Actions.Dispatch(DeviceActions.StylusToolSwap);

        Assert.Equal(["dialog", "page"], calls);
    }

    [Fact]
    public async Task Other_names_and_disposed_handlers_run_nothing()
    {
        var calls = 0;
        Actions.Handle(DeviceActions.StylusToolSwap, () => { calls++; return Task.CompletedTask; }).Dispose();

        await Actions.Dispatch(DeviceActions.StylusToolSwap);
        await Actions.Dispatch("action_something-else");

        Assert.Equal(0, calls);
    }

    [Fact]
    public void The_first_handler_starts_listening_to_the_shell_once()
    {
        Actions.Handle(DeviceActions.StylusToolSwap, () => Task.CompletedTask);
        Actions.Handle(DeviceActions.StylusToolSwap, () => Task.CompletedTask);

        Assert.Single(JSInterop.Invocations, i => i.Identifier == "solo.deviceActions.init");
    }
}
