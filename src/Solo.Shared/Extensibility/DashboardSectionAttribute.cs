namespace Solo.Shared.Extensibility;

/// <summary>
/// Marks a component rendered as its own collapsible section of the Your Tasks
/// dashboard, above the personal board, under a header core draws from
/// <see cref="Title"/> (contract 1.13). Components receive no parameters: the
/// dashboard is the signed-in user's, and the tasks it shows come from
/// <c>GET api/users/me/tasks</c>, which the component can fetch itself. Each
/// section has an error boundary; only loaded extension assemblies can contribute.
/// </summary>
/// <remarks>
/// <para>Core remembers whether each person folded the section away, so a
/// section that is expensive to draw should still load lazily: a collapsed one
/// is not rendered at all.</para>
///
/// <para>To stay current, subscribe to the user topic
/// <c>BoardHubConstants.AssignedTasksTopic</c> through
/// <see cref="ISoloLiveTopics.SubscribeUserAsync"/>: core nudges it whenever a
/// task assigned to the person, or the board, labels or project around one,
/// changes.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class DashboardSectionAttribute(string title) : Attribute
{
    public string Title { get; } = title;

    /// <summary>Ascending among contributed sections; ties break by extension id.</summary>
    public int Order { get; set; }
}
