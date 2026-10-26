namespace Solo.DesignDocs.Client;

internal static class OfflineEvents
{
    public static event Action<Guid>? Changed;
    public static void Raise(Guid projectId)=>Changed?.Invoke(projectId);
}
