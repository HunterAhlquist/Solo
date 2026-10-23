namespace Solo.DesignDocs.Client;

/// <summary>
/// Tells this tab's lists that a project's vaults changed, ahead of the live
/// topic's nudge. Static for the reason RoadmapNavEvents is: a client half
/// can't register services, and everything here runs in the one tab.
/// </summary>
public static class DocsEvents
{
    public static event Action<Guid>? VaultsChanged;

    public static void RaiseVaultsChanged(Guid projectId) => VaultsChanged?.Invoke(projectId);
}
