using MudBlazor;
using Solo.Shared.Extensibility;

namespace KitSpike.Client.Contributions;

/// <summary>
/// What an extension adds to a task's menu. A class, not a component: the host
/// creates it for each menu, asks it for items, and calls it with the one
/// chosen. One item only says something, one opens the host's task picker —
/// a dialog, which a client drawing the menu itself has to find a way to show
/// — and one is marked as dangerous.
/// </summary>
[TaskMenuContributor(Order = 90)]
public sealed class DemoTaskMenu(ISnackbar snackbar, ISoloTaskPicker picker) : ISoloTaskMenuContributor
{
    public ValueTask<IReadOnlyList<ContextMenuItem>> ItemsAsync(TaskMenuContext context, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ContextMenuItem>>(
        [
            new ContextMenuItem("hello", $"Say hello to #{context.Task.Number}"),
            new ContextMenuItem("pick", "Link another task…") { Disabled = !context.CanWrite },
            new ContextMenuItem("danger", "Forget its links") { Danger = true, SeparatorBefore = true },
        ]);

    public async Task OnChosenAsync(string itemId, TaskMenuContext context)
    {
        switch (itemId)
        {
            case "hello":
                snackbar.Add($"Hello from {context.Task.Name}.", Severity.Success);
                break;
            case "pick":
                var picked = await picker.PickAsync(context.ProjectId, new TaskPickerOptions { Title = "Link a task" });
                snackbar.Add(picked is null ? "Nothing linked." : $"Linked {picked.Name}.", picked is null ? Severity.Info : Severity.Success);
                break;
            case "danger":
                snackbar.Add("Links forgotten.", Severity.Warning);
                break;
        }
    }
}
