namespace Solo.Shared.Extensibility;

/// <summary>
/// Marks a component rendered on every task card on a board, in the card's
/// bottom row beside the deadline ring (contract 1.8). Components receive Task
/// (TaskDto), ProjectId (Guid) and CanWrite (bool). Each has an error boundary;
/// only loaded extension assemblies can contribute.
/// </summary>
/// <remarks>
/// <para>For a glance, not a form: a badge, a chip, a count. It renders once
/// per card, and a board can show hundreds, so load per board rather than per
/// card — one fetch shared by the board's cards through a static cache in your
/// assembly, keyed by board id and refreshed from a live topic. A card that
/// fetches for itself turns opening a board into a request per task.</para>
///
/// <para>The card opens the task drawer when clicked, so anything interactive
/// inside it needs <c>@onclick:stopPropagation</c>. Render nothing when there
/// is nothing to say; an empty contribution takes no space.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TaskCardExtensionAttribute : Attribute
{
    /// <summary>Ascending among contributions on a card; ties break by extension id.</summary>
    public int Order { get; set; }
}
