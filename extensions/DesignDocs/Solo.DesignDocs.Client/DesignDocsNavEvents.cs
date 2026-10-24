namespace Solo.DesignDocs.Client;

// Live broadcasts exclude the sender. Refresh local lists after mutations.
internal static class DesignDocsNavEvents
{
    public static event Action<Guid>? Changed;
    public static Guid? ActiveVault {get;private set;}
    public static void Activate(Guid projectId,Guid vault){ActiveVault=vault;Raise(projectId);}
    public static void Raise(Guid projectId) => Changed?.Invoke(projectId);
}
