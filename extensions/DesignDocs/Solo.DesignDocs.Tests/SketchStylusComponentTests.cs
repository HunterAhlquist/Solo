using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.State;
using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Tests;

/// <summary>
/// The Apple Pencil double-tap as the sketch receives it: the host's
/// <see cref="DeviceActions.StylusToolSwap"/>, which swaps to the eraser and
/// back to the tool in hand before it.
/// </summary>
public sealed class SketchStylusComponentTests : ComponentTestBase
{
    private (IRenderedComponent<MudBlazor.MudDialogProvider> Dialogs, SketchDialog Sketch, BunitJSModuleInterop Module) Open(bool canWrite = true)
    {
        var id = Guid.NewGuid();
        var http = new StubHttp();
        var live = new FakeLiveTopics();
        http.RouteJson("api/users/me",
            new Solo.Shared.Dtos.UserDto(Guid.NewGuid(), "alex@example.test", "Alex", null, false));
        http.Route("colors", System.Net.HttpStatusCode.OK, () => new ColorDto(Guid.NewGuid(), 0), HttpMethod.Post);
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());
        Services.AddSingleton<ISoloLiveTopics>(live);

        var module = JSInterop.SetupModule(_ => true);
        var sketch = module.SetupModule("mountSketch", _ => true);
        sketch.SetupVoid("command", _ => true).SetVoidResult();

        var dialogs = Render<MudBlazor.MudDialogProvider>();
        dialogs.InvokeAsync(() => Services.GetRequiredService<MudBlazor.IDialogService>()
            .ShowAsync<SketchDialog>("Map", new MudBlazor.DialogParameters<SketchDialog>
            {
                { x => x.ProjectId, Guid.NewGuid() },
                { x => x.VaultId, Guid.NewGuid() },
                { x => x.Sketch, new SketchDto(id, Guid.NewGuid(), "Map", "A4", "portrait", "dots", null, null, 1) },
                { x => x.CanWrite, canWrite },
            }));
        dialogs.WaitForState(() => live.Held.Any(h => h.Topic == DocTopics.Doc(id)));
        var instance = dialogs.FindComponent<SketchDialog>().Instance;
        dialogs.InvokeAsync(() => instance.Writable(true));
        return (dialogs, instance, sketch);
    }

    private Task DoubleTap(IRenderedComponent<MudBlazor.MudDialogProvider> dialogs) =>
        dialogs.InvokeAsync(() => ((DeviceActionsService)Services.GetRequiredService<ISoloDeviceActions>())
            .Dispatch(DeviceActions.StylusToolSwap));

    private static List<string?> Tools(BunitJSModuleInterop module) =>
        module.Invocations.Where(i => i.Identifier == "command" && i.Arguments[0] as string == "tool")
            .Select(i => i.Arguments[1] as string).ToList();

    private static bool Lit(IRenderedComponent<MudBlazor.MudDialogProvider> dialogs, string label) =>
        dialogs.FindAll($"[aria-label='{label}']").Any(e => e.ClassList.Contains("is-selected"));

    [Fact]
    public async Task A_double_tap_swaps_to_the_eraser_and_back_to_the_tool_in_hand()
    {
        var (dialogs, _, module) = Open();
        dialogs.Find("[aria-label='Rectangle']").Click();

        await DoubleTap(dialogs);
        dialogs.WaitForAssertion(() => Assert.True(Lit(dialogs, "Eraser")));
        await DoubleTap(dialogs);

        dialogs.WaitForAssertion(() => Assert.True(Lit(dialogs, "Rectangle")));
        Assert.Equal(["rectangle", "eraser", "rectangle"], Tools(module));
    }

    [Fact]
    public async Task A_read_only_sketch_ignores_the_double_tap()
    {
        var (dialogs, _, module) = Open(canWrite: false);

        await DoubleTap(dialogs);

        Assert.Empty(Tools(module));
    }

    [Fact]
    public async Task Closing_the_sketch_lets_go_of_the_double_tap()
    {
        var (dialogs, sketch, module) = Open();
        await dialogs.InvokeAsync(() => sketch.DisposeAsync().AsTask());

        await DoubleTap(dialogs);

        Assert.Empty(Tools(module));
    }
}
