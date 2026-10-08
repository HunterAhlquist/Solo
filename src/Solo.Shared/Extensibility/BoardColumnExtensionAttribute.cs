namespace Solo.Shared.Extensibility;

/// <summary>
/// Marks a component rendered beside each column's title in a board's header,
/// after core's own count (contract 1.8). Components receive Board (BoardDto),
/// Status (TaskState), Tasks (IReadOnlyList&lt;TaskDto&gt;: every task in that
/// column across the board's lanes, finished ones a folded column hides
/// included) and CanWrite (bool). Each has an error boundary; only loaded
/// extension assemblies can contribute.
/// </summary>
/// <remarks>
/// <c>Tasks</c> is the board's own state, so a count needs no request: a WIP
/// limit renders <c>Tasks.Count</c> against its own number. The phone layout
/// hides the board-wide header, core's counts with it, so this doesn't show
/// there either.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BoardColumnExtensionAttribute : Attribute
{
    /// <summary>Ascending among contributions in a column header; ties break by extension id.</summary>
    public int Order { get; set; }
}
