namespace Solo.Shared.Extensibility;

/// <summary>
/// Marks a component rendered as its own section of a project's home page, in
/// the first tab after the project's boards, under a header core draws from
/// <see cref="Title"/>. Components receive Project (ProjectSummaryDto) and
/// CanWrite (bool). Each section has an error boundary; only loaded extension
/// assemblies can contribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ProjectHomeSectionAttribute(string title) : Attribute
{
    public string Title { get; } = title;

    /// <summary>Ascending among contributed sections; ties break by extension id.</summary>
    public int Order { get; set; }
}
