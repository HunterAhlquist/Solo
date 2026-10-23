using MudBlazor;
using Solo.DesignDocs.Contracts;

namespace Solo.DesignDocs.Client;

/// <summary>The package's own small dialogs: the host's live in Solo.Client, which an extension can't reference.</summary>
public static class DocsDialogs
{
    private static readonly DialogOptions Small = new() { MaxWidth = MaxWidth.ExtraSmall, FullWidth = true };

    /// <summary>A name, trimmed, or null when cancelled.</summary>
    public static async Task<string?> AskNameAsync(IDialogService dialogs, string title, string initial, string saveText,
        string label = "Name", string? warning = null)
    {
        var dialog = await dialogs.ShowAsync<NameDialog>(title, new DialogParameters<NameDialog>
        {
            { d => d.Initial, initial },
            { d => d.SaveText, saveText },
            { d => d.Label, label },
            { d => d.Warning, warning },
        }, Small);
        return await dialog.Result is { Canceled: false, Data: string name } ? name : null;
    }

    public static async Task<bool> ConfirmAsync(IDialogService dialogs, string title, string message, string confirmText)
    {
        var dialog = await dialogs.ShowAsync<ConfirmDialog>(title, new DialogParameters<ConfirmDialog>
        {
            { d => d.Message, message },
            { d => d.ConfirmText, confirmText },
        }, Small);
        return await dialog.Result is { Canceled: false };
    }

    /// <summary>The folder to move into (null for the root), or Cancel when the person backed out.</summary>
    public static async Task<(bool Chosen, Guid? FolderId)> PickFolderAsync(IDialogService dialogs, string title,
        VaultTreeDto tree, Guid? current, Guid? exclude)
    {
        var dialog = await dialogs.ShowAsync<MoveDialog>(title, new DialogParameters<MoveDialog>
        {
            { d => d.Tree, tree },
            { d => d.Current, current },
            { d => d.Exclude, exclude },
        }, Small);
        var result = await dialog.Result;
        return result is { Canceled: false } ? (true, result.Data as Guid?) : (false, null);
    }
}
