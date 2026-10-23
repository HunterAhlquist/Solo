using Solo.Shared.Dtos;

namespace Solo.Shared.Extensibility;

/// <summary>
/// The host's task picker: a dialog that searches a project's tasks across its
/// boards, grouped by board and then by epic in board order.
/// </summary>
/// <remarks>
/// <para>Core draws it because core knows how a board orders its epics and
/// epic groups. An extension building its own picker would repeat that and
/// drift from it as the board changes. Inject this into a client page; a client
/// half can't register services of its own.</para>
///
/// <para>It reads through the core API as the signed-in user, so it offers
/// only what they can already see. Picking is not writing: whatever the caller
/// does with the task next still goes through its own endpoint's checks.</para>
/// </remarks>
public interface ISoloTaskPicker
{
    /// <summary>
    /// Opens the picker over the current page and waits for a choice. Returns
    /// null if the person cancels, or if <paramref name="ct"/> is cancelled,
    /// which closes the dialog.
    /// </summary>
    Task<TaskDto?> PickAsync(Guid projectId, TaskPickerOptions? options = null, CancellationToken ct = default);
}

/// <summary>What the picker asks for and leaves out.</summary>
public sealed record TaskPickerOptions
{
    /// <summary>Initially include completed work, for references rather than scheduling.</summary>
    public bool IncludeFinished { get; init; }

    /// <summary>Initially include closed boards. The person may change either filter.</summary>
    public bool IncludeClosedBoards { get; init; }

    /// <summary>The dialog's title. "Pick a task" when not given.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Tasks not to offer at all, such as the ones already linked to whatever
    /// the caller is adding to.
    /// </summary>
    public IReadOnlySet<Guid> Exclude { get; init; } = new HashSet<Guid>();

    /// <summary>
    /// What the picker says when the project has tasks but <see cref="Exclude"/>
    /// covers every one, such as "Every task is already on this roadmap."
    /// </summary>
    public string? AllExcludedText { get; init; }
}
