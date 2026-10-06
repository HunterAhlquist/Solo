using Microsoft.JSInterop;
using Solo.Shared.Extensibility;

namespace Solo.Client.State;

/// <summary>
/// The host side of <see cref="ISoloContextMenu"/>. Inside the desktop shell it
/// asks the shell for a native menu; anywhere else, or on a shell too old to
/// have one, it holds the menu for <c>ContextMenuHost</c> to draw.
/// </summary>
public sealed class ContextMenuService(IJSRuntime js) : ISoloContextMenu
{
    private TaskCompletionSource<string?>? _choice;

    /// <summary>The menu the host draws; null while none is open.</summary>
    public OpenContextMenu? Current { get; private set; }

    /// <summary>What the host renders changed.</summary>
    public event Action? StateChanged;

    public async Task<string?> ShowAsync(
        double clientX, double clientY, IReadOnlyList<ContextMenuItem> items, CancellationToken ct = default)
    {
        Dismiss();
        if (items.Count == 0 || ct.IsCancellationRequested)
        {
            return null;
        }

        if (await NativeAsync(items, ct) is { } native)
        {
            return native.Chosen;
        }

        var choice = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _choice = choice;
        Current = new OpenContextMenu(clientX, clientY, items);
        StateChanged?.Invoke();

        await using var dismissOnCancel = ct.Register(() => Resolve(choice, null));
        return await choice.Task;
    }

    /// <summary>The host's report of an item chosen from the drawn menu.</summary>
    public void Choose(string id)
    {
        if (_choice is { } choice && Current?.Items.Any(i => i.Id == id && !i.Disabled) == true)
        {
            Resolve(choice, id);
        }
    }

    /// <summary>Closes the drawn menu with nothing chosen.</summary>
    public void Dismiss()
    {
        if (_choice is { } choice)
        {
            Resolve(choice, null);
        }
    }

    private void Resolve(TaskCompletionSource<string?> choice, string? id)
    {
        if (!choice.TrySetResult(id) || _choice != choice)
        {
            return;
        }

        _choice = null;
        Current = null;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// The shell's menu, or null when there's no shell to ask — which includes
    /// a test host or a page without JS, where the drawn menu is what's left.
    /// </summary>
    /// <remarks>
    /// A native menu can't be closed from here, so a cancelled caller stops
    /// waiting and whatever the person picks afterwards goes nowhere.
    /// </remarks>
    private async Task<NativeResult?> NativeAsync(IReadOnlyList<ContextMenuItem> items, CancellationToken ct)
    {
        try
        {
            var request = items.Select(i => new NativeItem(i.Id, i.Label, !i.Disabled, i.SeparatorBefore)).ToList();
            var result = await js.InvokeAsync<NativeChoice?>("solo.contextMenu.native", ct, [request]);
            return result is null ? null : new NativeResult(result.Id);
        }
        catch (OperationCanceledException)
        {
            return new NativeResult(null);
        }
        catch (JSException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record NativeItem(string Id, string Label, bool Enabled, bool SeparatorBefore);

    private sealed record NativeChoice(string? Id);

    private sealed record NativeResult(string? Chosen);
}

/// <summary>A drawn menu: where it opens, and what's on it.</summary>
public sealed record OpenContextMenu(double ClientX, double ClientY, IReadOnlyList<ContextMenuItem> Items);
