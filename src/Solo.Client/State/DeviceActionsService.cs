using Microsoft.JSInterop;
using Solo.Shared.Extensibility;

namespace Solo.Client.State;

/// <summary>
/// The host side of <see cref="ISoloDeviceActions"/>. The app shell dispatches
/// a <c>solo:device-action</c> event on the window; <c>solo.deviceActions</c>
/// forwards it here, and the newest handler for that name runs.
/// </summary>
public sealed class DeviceActionsService(IJSRuntime js) : ISoloDeviceActions, IDisposable
{
    private readonly List<Registration> _handlers = [];
    private DotNetObjectReference<DeviceActionsService>? _reference;

    public IDisposable Handle(string actionId, Func<Task> handler)
    {
        var registration = new Registration(actionId, handler, this);
        _handlers.Add(registration);
        if (_reference is null)
        {
            _reference = DotNetObjectReference.Create(this);
            _ = ListenAsync(_reference);
        }

        return registration;
    }

    [JSInvokable]
    public Task Dispatch(string actionId) =>
        _handlers.LastOrDefault(h => h.ActionId == actionId)?.Handler() ?? Task.CompletedTask;

    public void Dispose() => _reference?.Dispose();

    // A test host or a page without the interop script has no shell to listen
    // to, which is the same as a browser: nothing ever arrives.
    private async Task ListenAsync(DotNetObjectReference<DeviceActionsService> reference)
    {
        try
        {
            await js.InvokeVoidAsync("solo.deviceActions.init", reference);
        }
        catch (JSException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class Registration(string actionId, Func<Task> handler, DeviceActionsService owner) : IDisposable
    {
        public string ActionId { get; } = actionId;
        public Func<Task> Handler { get; } = handler;

        public void Dispose() => owner._handlers.Remove(this);
    }
}
