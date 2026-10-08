namespace Solo.Shared.Extensibility;

/// <summary>
/// Marks a component rendered as its own section of a project's Settings tab,
/// below core's own settings, under a header core draws from
/// <see cref="Title"/> (contract 1.8). Components receive Project
/// (ProjectSummaryDto) and CanWrite (bool). Each section has an error
/// boundary; only loaded extension assemblies can contribute.
/// </summary>
/// <remarks>
/// The tab only shows to the project's owners, but that is a hiding place, not
/// a guard: the section saves through your own endpoint, which checks for
/// itself, usually with <c>SoloPolicies.ProjectOwner</c> on a route carrying
/// the project id. Core's settings above it save with their own button; a
/// section saves on its own terms, so say on it when a change takes effect.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ProjectSettingsSectionAttribute(string title) : Attribute
{
    public string Title { get; } = title;

    /// <summary>Ascending among contributed sections; ties break by extension id.</summary>
    public int Order { get; set; }
}
