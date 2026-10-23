namespace Solo.Shared.Extensibility;

/// <summary>Requests space for an extension page without depending on the host's layout markup.</summary>
public interface ISoloWorkspace
{
    /// <summary>
    /// Applies these options until the returned lease is disposed, usually when
    /// the page leaves. Nested workspaces restore the previous one's options.
    /// Collapsing navigation is an initial preference: the person can reopen it.
    /// </summary>
    IDisposable Enter(WorkspaceOptions options);
}

/// <summary>How a page uses the host's content area.</summary>
public sealed record WorkspaceOptions(bool FullWidth = false, bool FillHeight = false, bool CollapseNavigation = false);
