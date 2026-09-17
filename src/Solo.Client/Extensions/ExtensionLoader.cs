using System.Reflection;
using Solo.Client.Api;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Extensions;

/// <summary>
/// What the app knows about extension client halves: which loaded, which were
/// offered and skipped, and the assemblies the router needs to resolve their
/// pages.
/// </summary>
/// <remarks>
/// <para><b>Filled after sign-in, not at boot.</b> The endpoints behind it are
/// authenticated, and on the web build the credential comes from
/// <c>GoogleAuthenticationStateProvider</c> — a service that does not exist
/// until the host is built. So the singleton starts empty, <c>App.razor</c>
/// fills it once there is an authenticated user, and the router re-scans routes
/// on the re-render that follows.</para>
///
/// <para>Mutable for that reason alone. Nothing removes an extension: an
/// assembly cannot be unloaded, and the app only ever goes from knowing nothing
/// to knowing what the instance serves.</para>
/// </remarks>
public sealed class ExtensionCatalog
{
    private IReadOnlyList<LoadedExtension> _loaded = [];
    private IReadOnlyList<SkippedExtension> _skipped = [];
    private IReadOnlyList<Assembly> _routeAssemblies = [];

    /// <summary>Raised when the catalog gains its contents, so the sidebar can redraw.</summary>
    public event Action? Changed;

    public IReadOnlyList<LoadedExtension> Loaded
    {
        get => _loaded;
        init => SetLoaded(value);
    }

    /// <summary>Extensions that were offered but not loaded, and why. Shown in admin settings.</summary>
    public IReadOnlyList<SkippedExtension> Skipped
    {
        get => _skipped;
        init => _skipped = value;
    }

    /// <summary>
    /// Assemblies to hand the router, so extension <c>@page</c> routes resolve.
    /// </summary>
    /// <remarks>
    /// Cached rather than projected per access: this is a <c>Router</c>
    /// parameter, so it is read on every render of <c>App</c>, and a fresh list
    /// each time would have the router hash it into a new <c>RouteKey</c> for
    /// nothing.
    /// </remarks>
    public IReadOnlyList<Assembly> RouteAssemblies => _routeAssemblies;

    /// <summary>Nav entries from extensions that actually loaded.</summary>
    public IEnumerable<ClientExtensionNavDto> Nav => _loaded.SelectMany(e => e.Descriptor.Nav);

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

    /// <summary>
    /// Publishes the result of a load. One call, so the router and the sidebar
    /// never see a half-filled catalog.
    /// </summary>
    public void Apply(IReadOnlyList<LoadedExtension> loaded, IReadOnlyList<SkippedExtension> skipped)
    {
        SetLoaded(loaded);
        _skipped = skipped;
        Changed?.Invoke();
    }

    private void SetLoaded(IReadOnlyList<LoadedExtension> loaded)
    {
        _loaded = loaded;
        _routeAssemblies = loaded.Select(e => e.Assembly).ToList();
    }

    private static bool IsProjectScoped(ClientExtensionNavDto entry) =>
        entry.Route.Contains(ProjectAliasToken, StringComparison.OrdinalIgnoreCase);
}

public sealed record LoadedExtension(ClientExtensionDto Descriptor, Assembly Assembly);

public sealed record SkippedExtension(string Id, string Reason);

/// <summary>
/// Fetches and loads extension client assemblies, so their pages are available
/// to the router.
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
/// fails to download, or fails to load, is skipped and recorded — the app runs
/// without it. Its nav entry is dropped with it, because a sidebar entry
/// pointing at a route no assembly registered renders the router's "that page
/// doesn't exist", which is indistinguishable from a bug.</para>
/// </remarks>
public static class ExtensionBootstrapper
{
    /// <summary>
    /// Downloads and loads every compatible extension client assembly, then
    /// publishes the result to <paramref name="catalog"/>.
    /// </summary>
    /// <param name="catalog">The app's singleton catalog, filled in place.</param>
    /// <param name="api">
    /// The app's authenticated client. Both endpoints require a signed-in
    /// caller, so this must not run before there is one.
    /// </param>
    public static async Task LoadAsync(
        ExtensionCatalog catalog, ApiClient api, CancellationToken ct = default)
    {
        List<ClientExtensionDto> descriptors;
        try
        {
            descriptors = await api.GetClientExtensionsAsync(ct);
        }
        catch (Exception e) when (IsTransport(e))
        {
            // An instance too old to have the endpoint, unreachable, or one that
            // no longer accepts our credential. None is a reason to tear down a
            // running app: extensions are additive.
            catalog.Apply([], []);
            return;
        }

        if (descriptors is { Count: 0 })
        {
            catalog.Apply([], []);
            return;
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
                var bytes = await api.GetExtensionAssemblyAsync(
                    descriptor.Id, $"{descriptor.ClientAssembly}.dll", ct);
                loaded.Add(new LoadedExtension(descriptor, Assembly.Load(bytes)));
            }
            catch (Exception e)
            {
                skipped.Add(new SkippedExtension(descriptor.Id, $"Failed to load: {e.Message}"));
            }
        }

        catalog.Apply(loaded, skipped);
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

    private static bool IsTransport(Exception e) =>
        e is HttpRequestException or TaskCanceledException or NotSupportedException
            or System.Text.Json.JsonException or ApiException;
}
