namespace Solo.DesignDocs.Client;

// Live broadcasts omit their sender. Lists in that browser need the same nudge
// after local mutations that other browsers receive over the vault-list topic.
internal static class DesignDocsNavEvents
{
    public static event Action<Guid>? Changed;
    public static void Raise(Guid projectId) => Changed?.Invoke(projectId);
}
