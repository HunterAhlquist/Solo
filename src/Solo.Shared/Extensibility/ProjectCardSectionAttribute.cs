namespace Solo.Shared.Extensibility;

/// <summary>
/// Marks a component rendered as its own surface on each project's card on the
/// All projects page, below the card's boards and titled from
/// <see cref="Title"/>. Components receive Project (ProjectSummaryDto) and
/// CanWrite (bool). Each surface has an error boundary; only loaded extension
/// assemblies can contribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ProjectCardSectionAttribute(string title) : Attribute
{
    public string Title { get; } = title;

    /// <summary>Ascending among contributed sections; ties break by extension id.</summary>
    public int Order { get; set; }
}
