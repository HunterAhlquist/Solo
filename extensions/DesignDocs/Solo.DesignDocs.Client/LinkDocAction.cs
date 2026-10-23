using MudBlazor;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Client;

/// <summary>
/// "Link a doc" in core's markdown editor (task and epic descriptions,
/// comments): pick a note, optionally a heading, and insert a link to it at
/// the cursor. The link carries the note's id, not its project or path, so it
/// survives renames, moves and an alias change.
/// </summary>
[MarkdownEditorAction(Order = 20)]
public sealed class LinkDocAction(IDialogService dialogs) : ISoloMarkdownEditorAction
{
    private const string ButtonId = "link-doc";

    private static readonly IReadOnlyList<MarkdownEditorButton> Buttons =
        [new(ButtonId, "Link a doc") { Icon = Icons.Material.Filled.MenuBook }];

    public ValueTask<IReadOnlyList<MarkdownEditorButton>> ButtonsAsync(MarkdownEditorContext context, CancellationToken ct) =>
        ValueTask.FromResult(Buttons);

    public async Task<string?> OnChosenAsync(string buttonId, MarkdownEditorContext context, CancellationToken ct)
    {
        if (buttonId != ButtonId)
        {
            return null;
        }

        var dialog = await dialogs.ShowAsync<DocLinkDialog>("Link a doc", new DialogParameters<DocLinkDialog>
        {
            { d => d.ProjectId, context.ProjectId },
            { d => d.SelectedText, context.SelectedText },
        }, new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        return await dialog.Result is { Canceled: false, Data: string markdown } ? markdown : null;
    }
}
