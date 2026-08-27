using Microsoft.JSInterop;

namespace Solo.Client.State;

/// <summary>
/// Where a drag was let go: which item, which drop zone, and the slot it landed
/// in. <paramref name="Index"/> counts the zone's <em>other</em> items, so it is
/// already an insertion point among them — see <see cref="RankNeighbors"/>.
/// </summary>
public sealed record DragDrop(string Group, string ItemId, string ZoneId, int Index)
{
    /// <summary>The dragged item's id, when the markup identifies items by GUID.</summary>
    public Guid? ItemGuid => Guid.TryParse(ItemId, out var id) ? id : null;
}

/// <summary>
/// The Blazor half of <c>wwwroot/js/drag.js</c>. The engine is entirely in the
/// browser — pointer tracking, hit testing, the card riding under the finger —
/// and surfaces here only at the end, as one call per completed drop.
///
/// Components subscribe by <em>group</em>, the name their markup puts in
/// <c>data-drag-group</c>: the board takes "task" and "epic", the checklist
/// "subtask", the sidebar "project". One handler per group, since only one
/// component ever renders a given group's zones.
/// </summary>
public sealed class DragBridge : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly Dictionary<string, Func<DragDrop, Task>> _handlers = new(StringComparer.Ordinal);
    private DotNetObjectReference<DragBridge>? _self;

    public DragBridge(IJSRuntime js) => _js = js;

    /// <summary>
    /// Starts listening for drops in <paramref name="group"/>. Safe to call on
    /// every render pass — handing the same handler over twice is a no-op.
    /// </summary>
    public async ValueTask RegisterAsync(string group, Func<DragDrop, Task> onDrop)
    {
        if (_handlers.TryGetValue(group, out var existing) && existing == onDrop)
        {
            return;
        }

        _handlers[group] = onDrop;

        if (_self is not null)
        {
            return;
        }

        var self = DotNetObjectReference.Create(this);
        try
        {
            await _js.InvokeVoidAsync("solo.drag.init", self);
            _self = self;
        }
        catch (JSException)
        {
            // The script hasn't loaded. Dragging is an enhancement — everything
            // it does has a dialog or a menu too — so this isn't fatal, but the
            // reference stays unset so the next component to register tries again.
            self.Dispose();
        }
    }

    /// <summary>
    /// Drops a subscription — but only the caller's own. A component being torn
    /// down after its replacement has already registered must not unhook it.
    /// </summary>
    public void Unregister(string group, Func<DragDrop, Task> onDrop)
    {
        if (_handlers.TryGetValue(group, out var current) && current == onDrop)
        {
            _handlers.Remove(group);
        }
    }

    [JSInvokable]
    public Task OnDropAsync(DragDrop drop) =>
        _handlers.TryGetValue(drop.Group, out var handler) ? handler(drop) : Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _self?.Dispose();
        _self = null;
        _handlers.Clear();
        return ValueTask.CompletedTask;
    }
}
