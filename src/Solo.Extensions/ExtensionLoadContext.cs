using System.Reflection;
using System.Runtime.Loader;

namespace Solo.Extensions;

/// <summary>
/// One load context per extension, so two extensions can carry different
/// versions of the same private dependency without colliding.
/// </summary>
/// <remarks>
/// <para><b>The load-bearing rule is <see cref="IsShared"/>.</b> Contract and
/// framework assemblies must resolve from the default context, never from a
/// private copy in the package. Load a private <c>Solo.Extensibility</c> and the
/// <c>ISoloOperationHook</c> the extension implements is not the
/// <c>ISoloOperationHook</c> the host dispatches — the classic plugin bug, and
/// it presents as a hook that silently never runs.</para>
///
/// <para>Non-collectible on purpose. Unloading would require every MVC and EF
/// reference into the extension to drop, which they will not, and the lifecycle
/// is restart-based anyway: there is no rescan for extensions.</para>
/// </remarks>
public sealed class ExtensionLoadContext(string extensionId, IReadOnlyDictionary<string, byte[]> dependencies)
    : AssemblyLoadContext($"solo-ext:{extensionId}", isCollectible: false)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null || IsShared(name))
        {
            // null defers to the default context.
            return null;
        }

        if (dependencies.TryGetValue(name, out var bytes))
        {
            return LoadFromStream(new MemoryStream(bytes, writable: false));
        }

        return null;
    }

    /// <summary>
    /// Assemblies that must be the host's instance rather than the package's.
    /// Anything a type could cross the boundary on belongs here.
    /// </summary>
    internal static bool IsShared(string name) =>
        name is "Solo.Extensibility" or "Solo.Shared" ||
        name.StartsWith("System.", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
        name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) ||
        name.StartsWith("Npgsql", StringComparison.Ordinal) ||
        name is "netstandard" or "mscorlib";
}
