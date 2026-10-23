namespace Solo.Shared.Extensibility;

/// <summary>
/// Adds buttons to core's markdown editor — task and epic descriptions, and the
/// comment composer — that insert text at the cursor (contract 1.10). A link to
/// something the extension owns, typically, chosen in its own dialog.
/// </summary>
/// <remarks>
/// <para>A class, not a component, like <see cref="ISoloTaskMenuContributor"/>.
/// Mark it with <see cref="MarkdownEditorActionAttribute"/>. The host creates it
/// from its own services each time an editor opens, so its constructor can take
/// <see cref="ISoloClientApi"/>, MudBlazor's <c>IDialogService</c> and the rest,
/// and disposes it when the editor closes if it is disposable.</para>
///
/// <para>The editor only opens for someone who can write what it edits, so the
/// buttons need no permission check of their own. What they insert is still
/// only text: the person can edit or delete it before saving, and anything it
/// links to is checked by its own endpoint when followed.</para>
/// </remarks>
public interface ISoloMarkdownEditorAction
{
    /// <summary>
    /// Buttons for this editor, from what the action already holds. Awaited
    /// before the toolbar renders: one that throws or takes over a second is
    /// left out, and the editor works without it.
    /// </summary>
    ValueTask<IReadOnlyList<MarkdownEditorButton>> ButtonsAsync(MarkdownEditorContext context, CancellationToken ct);

    /// <summary>
    /// Text to insert at the cursor, replacing any selection, or null to insert
    /// nothing (a dialog the person cancelled). Text that would take the field
    /// past its length limit is refused with a message rather than cut.
    /// </summary>
    Task<string?> OnChosenAsync(string buttonId, MarkdownEditorContext context, CancellationToken ct);
}

/// <summary>Marks an <see cref="ISoloMarkdownEditorAction"/> for the host to find.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class MarkdownEditorActionAttribute : Attribute
{
    /// <summary>Ascending among actions; ties break by extension id.</summary>
    public int Order { get; set; }
}

/// <summary>One button in the editor's toolbar. <see cref="Id"/> is the action's own; the host keeps two actions' ids apart.</summary>
public sealed record MarkdownEditorButton(string Id, string Label)
{
    /// <summary>An SVG icon, such as one of MudBlazor's <c>Icons.Material</c> strings. Without one the label shows as text.</summary>
    public string? Icon { get; init; }
}

/// <summary>What an editor is editing.</summary>
/// <param name="EntityId">The task, epic or (for a new comment) task the text belongs to.</param>
/// <param name="SelectedText">The selection when the button was pressed, or null.</param>
public sealed record MarkdownEditorContext(Guid ProjectId, MarkdownSurface Surface, Guid EntityId, string? SelectedText);

/// <summary>Which of core's fields an editor edits.</summary>
public enum MarkdownSurface
{
    TaskDescription = 0,
    EpicDescription = 1,
    Comment = 2,
}
