using Solo.Shared.Extensibility;

namespace Solo.Client.State;

/// <summary>The host's workspace leases, kept in this browser's scoped services.</summary>
public sealed class WorkspaceState : ISoloWorkspace
{
    private readonly List<Lease> _leases = [];
    public WorkspaceOptions Options => _leases.LastOrDefault()?.Options ?? new();
    public bool IsActive => _leases.Count > 0;
    public event Action? Changed;

    public IDisposable Enter(WorkspaceOptions options)
    {
        var lease = new Lease(this, options);
        _leases.Add(lease);
        Changed?.Invoke();
        return lease;
    }

    private sealed class Lease(WorkspaceState owner, WorkspaceOptions options) : IDisposable
    {
        public WorkspaceOptions Options { get; } = options;
        public void Dispose()
        {
            if (owner._leases.Remove(this)) owner.Changed?.Invoke();
        }
    }
}
