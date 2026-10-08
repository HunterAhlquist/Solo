namespace Solo.Shared.Extensibility;

/// <summary>
/// Marks a component rendered as its own section of a board's settings dialog,
/// below its name, alias and icon, under a header core draws from
/// <see cref="Title"/> (contract 1.8). Components receive Board (BoardDto) and
/// CanWrite (bool). Each section has an error boundary; only loaded extension
/// assemblies can contribute.
/// </summary>
/// <remarks>
/// <para>The dialog's Save button saves core's fields only. A section saves
/// through your own endpoint, on its own control or as it changes, and that
/// endpoint checks write access for itself — a route carrying the board id can
/// use <c>SoloPolicies.ProjectWrite</c>.</para>
///
/// <para>Per-board values fit the board's extension field bag
/// (<c>IExtensionFieldStore</c> with <c>ExtensionEntity.Board</c>), which needs
/// no schema of your own.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BoardSettingsSectionAttribute(string title) : Attribute
{
    public string Title { get; } = title;

    /// <summary>Ascending among contributed sections; ties break by extension id.</summary>
    public int Order { get; set; }
}
