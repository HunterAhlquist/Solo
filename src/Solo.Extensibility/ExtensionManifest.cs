using System.Text.Json.Serialization;
using Solo.Shared.Extensibility;

namespace Solo.Extensibility;

/// <summary>
/// <c>extension.json</c> at the root of a package. Parsed by the host; also
/// readable by an extension that wants its own declared metadata.
/// </summary>
public sealed class ExtensionManifest
{
    /// <summary>Lower-case identifier: the route segment and the schema namespace.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>Server contract range, checked against <see cref="ContractVersion"/> at load.</summary>
    public string? SoloApiVersion { get; set; }

    /// <summary>
    /// Client contract range, checked in the browser against the running client
    /// build. Separate from <see cref="SoloApiVersion"/> because the Electron
    /// shell bundles its own client and can be older than the instance.
    /// </summary>
    public string? SoloClientVersion { get; set; }

    /// <summary>Relative path of the server assembly; omit for a client-only extension.</summary>
    public string? Server { get; set; }

    /// <summary>Relative path of the client assembly; omit for a server-only extension.</summary>
    public string? Client { get; set; }

    /// <summary>Postgres schema this extension owns. Omit when it stores nothing.</summary>
    public string? Schema { get; set; }

    public List<ExtensionNavEntry> Nav { get; set; } = [];

    /// <summary>
    /// Operations this extension's hooks may run on. Declared up front so the
    /// admin page can show which extensions sit on which write path without
    /// loading anything.
    /// </summary>
    public List<string> Hooks { get; set; } = [];

    /// <summary>
    /// When true a Before hook that throws or times out is skipped instead of
    /// failing the request. Opt-in, for advisory hooks only.
    /// </summary>
    public bool FailOpen { get; set; }
}

/// <summary>A sidebar entry, rendered from the manifest without loading the client assembly.</summary>
public sealed class ExtensionNavEntry
{
    /// <summary>Client route. <c>{projectAlias}</c> is substituted per project.</summary>
    public string Route { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>Material icon name, resolved the same way project/board icons are.</summary>
    public string? Icon { get; set; }

    /// <summary>Minimum project access the viewer needs.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExtensionAccess Requires { get; set; } = ExtensionAccess.Read;
}

/// <summary>Access level a nav entry or endpoint requires; mirrors the core project policies.</summary>
public enum ExtensionAccess
{
    Read = 0,
    Write = 1,
    Owner = 2,
}
