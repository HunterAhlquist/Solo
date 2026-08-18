using System.Collections.Concurrent;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>
/// The set of extensions this instance loaded, and the live enabled/disabled
/// flag for each.
/// </summary>
/// <remarks>
/// <para>The flag exists because of an ordering trap: services — controllers,
/// hooks, DbContexts — are registered before <c>builder.Build()</c>, but
/// migrations run after it. By the time a migration fails, the extension's
/// routes are already mapped and its hooks already registered. Without a gate
/// consulted at request time it would serve traffic against tables that do not
/// exist.</para>
///
/// <para>Singleton, mutated only during startup and read on every extension
/// request, hence the concurrent map.</para>
/// </remarks>
public sealed class ExtensionState
{
    private readonly ConcurrentDictionary<string, string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    public ExtensionState(IReadOnlyList<ExtensionPackage> packages, IReadOnlyList<ExtensionLoadFailure> failures)
    {
        Packages = packages;
        Failures = failures;
    }

    /// <summary>Packages whose server half loaded far enough to register services.</summary>
    public IReadOnlyList<ExtensionPackage> Packages { get; }

    /// <summary>Packages that never loaded, and why. Reported in Admin settings.</summary>
    public IReadOnlyList<ExtensionLoadFailure> Failures { get; }

    /// <summary>All manifests, including of extensions currently disabled.</summary>
    public IEnumerable<ExtensionManifest> Manifests => Packages.Select(p => p.Manifest);

    public ExtensionPackage? Find(string id) =>
        Packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Disables an extension for the life of the process, with a reason a caller can be told.</summary>
    public void Disable(string extensionId, string reason) => _disabled[extensionId] = reason;

    /// <summary>
    /// Clears a disable. Startup never needs this — a disabled extension stays
    /// disabled for the life of the process — but tests that exercise the gate
    /// need to put the shared singleton back.
    /// </summary>
    public void Enable(string extensionId) => _disabled.TryRemove(extensionId, out _);

    public bool IsDisabled(string extensionId, out string reason)
    {
        var found = _disabled.TryGetValue(extensionId, out var stored);
        reason = stored ?? string.Empty;
        return found;
    }

    /// <summary>Enabled extensions only — what the client is told about and what hooks dispatch for.</summary>
    public IEnumerable<ExtensionPackage> Enabled =>
        Packages.Where(p => !_disabled.ContainsKey(p.Id));
}
