using MudBlazor;
using Solo.Client.Components;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.State;

/// <summary>
/// The host side of <see cref="ISoloTaskPicker"/>: shows <see cref="TaskPickerDialog"/>
/// through the layout's dialog provider, as any core dialog is shown.
/// </summary>
public sealed class TaskPickerService(IDialogService dialogs) : ISoloTaskPicker
{
    public async Task<TaskDto?> PickAsync(
        Guid projectId, TaskPickerOptions? options = null, CancellationToken ct = default)
    {
        options ??= new TaskPickerOptions();
        var dialog = await dialogs.ShowAsync<TaskPickerDialog>(
            options.Title ?? "Pick a task",
            new DialogParameters<TaskPickerDialog>
            {
                { d => d.ProjectId, projectId },
                { d => d.Options, options },
            },
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });

        // A caller tearing its page down shouldn't leave a dialog behind that
        // picks into nothing.
        await using var closeOnCancel = ct.Register(() => dialog.Close(DialogResult.Cancel()));
        var result = await dialog.Result;
        return result is { Canceled: false, Data: TaskDto task } ? task : null;
    }
}
