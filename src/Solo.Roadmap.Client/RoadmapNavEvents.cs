namespace Solo.Roadmap.Client;

/// <summary>
/// Tells the sidebar section that a project's roadmaps changed — created,
/// renamed or deleted on the roadmap page.
/// </summary>
/// <remarks>
/// Static because a client half can't register services: its assembly loads
/// after the container is built, so there is no scoped notifier to inject.
/// Everything in this assembly runs in the one browser tab, so a static event
/// reaches exactly the components that should hear it. Subscribers unsubscribe
/// in <c>Dispose</c>.
/// </remarks>
public static class RoadmapNavEvents
{
    /// <summary>Raised with the project whose roadmaps changed.</summary>
    public static event Action<Guid>? Changed;

    public static void Raise(Guid projectId) => Changed?.Invoke(projectId);
}
