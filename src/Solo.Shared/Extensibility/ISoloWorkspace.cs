namespace Solo.Shared.Extensibility;

public interface ISoloWorkspace
{
    /// <summary>
    /// Dispose the lease when the page closes. Nested leases restore the previous options.
    /// CollapseNavigation applies only on entry. The person can reopen navigation.
    /// </summary>
    IDisposable Enter(WorkspaceOptions options);
}

public sealed record WorkspaceOptions(bool FullWidth = false, bool FillHeight = false, bool CollapseNavigation = false);
