using System.Reflection;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>A package read off disk: its manifest, its assemblies' bytes, and its static content.</summary>
/// <remarks>
/// Assemblies are held as bytes rather than paths because the client half is
/// served over HTTP and the server half is loaded through a private load
/// context; neither wants a file handle held open for the life of the process.
/// </remarks>
public sealed class ExtensionPackage
{
    public required ExtensionManifest Manifest { get; init; }

    /// <summary>Zip filename or directory name — what an operator sees in a failure message.</summary>
    public required string Source { get; init; }

    public string Id => Manifest.Id;

    /// <summary>Server assembly bytes, or null for a client-only extension.</summary>
    public byte[]? ServerAssembly { get; init; }

    /// <summary>Private dependencies of the server assembly, by file name.</summary>
    public IReadOnlyDictionary<string, byte[]> ServerDependencies { get; init; } =
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Client assembly bytes, served to the browser. Null for a server-only extension.</summary>
    public byte[]? ClientAssembly { get; init; }

    public IReadOnlyDictionary<string, byte[]> ClientDependencies { get; init; } =
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files under <c>content/</c>, by relative path.</summary>
    public IReadOnlyDictionary<string, ExtensionContent> Content { get; init; } =
        new Dictionary<string, ExtensionContent>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set once the server assembly is loaded and its entry point found.</summary>
    public Assembly? LoadedAssembly { get; set; }
}

/// <summary>One static file from a package's <c>content/</c> folder.</summary>
public sealed record ExtensionContent(string Path, string ContentType, byte[] Bytes);

/// <summary>A package that could not be loaded, and why. Surfaced in Admin settings.</summary>
public sealed record ExtensionLoadFailure(string Source, string Reason);

/// <summary>Thrown by the loader for a malformed, oversized or invalid package.</summary>
public sealed class ExtensionPackageException(string message) : Exception(message);
