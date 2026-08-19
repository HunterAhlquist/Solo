using System.Net.Http.Json;
using System.Reflection;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Extensions;

/// <summary>
/// Fetches and loads extension client assemblies before the host is built, so
/// their pages are available to the router on the first render.
/// </summary>
/// <remarks>
/// <para><b>The compat check is the point of this class.</b> The server already
/// checked each package's <c>soloApiVersion</c> against its own contract, but
/// that says nothing about the client: the Electron shell serves a
/// <c>Solo.Client</c> bundled with the app and proxies <c>/api/*</c> to whatever
/// instance is configured, and the two version independently (<c>server-v*</c>
/// vs <c>desktop-v*</c>). So an assembly compiled against a newer contract than
/// this build can arrive, and loading it would throw
/// <c>MissingMethodException</c> at first render — on desktop only, for users
/// who have not updated.</para>
///
/// <para>Everything here fails soft. An extension that fails the range check, or
/// fails to download, or fails to load, is skipped and recorded — the app boots
/// without it. Its nav entry is dropped with it, because a sidebar entry
/// pointing at a route no assembly registered renders the router's "that page
/// doesn't exist", which is indistinguishable from a bug.</para>
/// </remarks>
public sealed class ExtensionCatalog
{
    public IReadOnlyList<LoadedExtension> Loaded { get; init; } = [];

    /// <summary>Extensions that were offered but not loaded, and why. Shown in admin settings.</summary>
    public IReadOnlyList<SkippedExtension> Skipped { get; init; } = [];

    /// <summary>Assemblies to hand the router, so extension <c>@page</c> routes resolve.</summary>
    public IReadOnlyList<Assembly> RouteAssemblies => Loaded.Select(e => e.Assembly).ToList();

    /// <summary>Nav entries from extensions that actually loaded.</summary>
    public IEnumerable<ClientExtensionNavDto> Nav => Loaded.SelectMany(e => e.Descriptor.Nav);

    /// <summary>
    /// Entries belonging to a project: their route carries <c>{projectAlias}</c>,
    /// so the sidebar renders one per project with the alias substituted, gated
    /// on the viewer's role in it.
    /// </summary>
    public IEnumerable<ClientExtensionNavDto> ProjectNav => Nav.Where(IsProjectScoped);

    /// <summary>
    /// Instance-level entries — no project in the route, so there is one of each
    /// and it sits in the sidebar footer beside Admin settings. A manifest's
    /// <c>requires</c> names a level of <em>project</em> access, so it means
    /// nothing here: an instance-level page authorizes itself.
    /// </summary>
    public IEnumerable<ClientExtensionNavDto> InstanceNav => Nav.Where(entry => !IsProjectScoped(entry));

    public const string ProjectAliasToken = "{projectAlias}";

    private static bool IsProjectScoped(ClientExtensionNavDto entry) =>
        entry.Route.Contains(ProjectAliasToken, StringComparison.OrdinalIgnoreCase);
}

public sealed record LoadedExtension(ClientExtensionDto Descriptor, Assembly Assembly);

public sealed record SkippedExtension(string Id, string Reason);

public static class ExtensionBootstrapper
{
    /// <summary>
    /// Downloads and loads every compatible extension client assembly.
    /// </summary>
    /// <param name="http">Anonymous bootstrap client, based at the instance.</param>
    public static async Task<ExtensionCatalog> LoadAsync(HttpClient http)
    {
        List<ClientExtensionDto>? descriptors;
        try
        {
            descriptors = await http.GetFromJsonAsync<List<ClientExtensionDto>>("api/extensions");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or NotSupportedException
                                      or System.Text.Json.JsonException)
        {
            // An instance too old to have the endpoint, or unreachable. Neither
            // is a reason to refuse to boot: extensions are additive.
            return new ExtensionCatalog();
        }

        if (descriptors is null or { Count: 0 })
        {
            return new ExtensionCatalog();
        }

        var loaded = new List<LoadedExtension>();
        var skipped = new List<SkippedExtension>();

        foreach (var descriptor in descriptors)
        {
            if (descriptor.ClientAssembly is null)
            {
                continue;
            }

            if (!IsCompatible(descriptor, out var reason))
            {
                skipped.Add(new SkippedExtension(descriptor.Id, reason!));
                continue;
            }

            try
            {
                var bytes = await http.GetByteArrayAsync(
                    $"api/extensions/{descriptor.Id}/client/{descriptor.ClientAssembly}.dll");
                loaded.Add(new LoadedExtension(descriptor, Assembly.Load(bytes)));
            }
            catch (Exception e)
            {
                skipped.Add(new SkippedExtension(descriptor.Id, $"Failed to load: {e.Message}"));
            }
        }

        return new ExtensionCatalog { Loaded = loaded, Skipped = skipped };
    }

    /// <summary>
    /// Checks the extension's declared client contract range against this
    /// build's, before any bytes are loaded.
    /// </summary>
    private static bool IsCompatible(ClientExtensionDto descriptor, out string? reason)
    {
        reason = null;
        if (!VersionRange.TryParse(descriptor.SoloClientVersion, out var range, out var error))
        {
            reason = $"Declares an invalid client version range: {error}";
            return false;
        }

        if (!range.Includes(ContractVersion.Parsed))
        {
            reason =
                $"Needs Solo client API {range}; this build provides {ContractVersion.Current}. " +
                "Update the app to use this extension.";
            return false;
        }

        return true;
    }
}
