namespace Solo.Shared.Extensibility;

/// <summary>
/// Marks a component rendered as its own section of each project's sidebar
/// group, after Boards, under a header core draws from <see cref="Title"/>.
/// Components receive Project (ProjectSummaryDto) and CanWrite (bool). Each
/// section has an error boundary; only loaded extension assemblies can contribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ProjectNavSectionAttribute(string title) : Attribute
{
    public string Title { get; } = title;

    /// <summary>Ascending among contributed sections; ties break by extension id.</summary>
    public int Order { get; set; }
}
