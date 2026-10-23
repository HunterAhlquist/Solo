using System.Net;
using Solo.Client.Extensions;
using Solo.Extension.Fixture.Client;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// The loader that turns what the instance advertises into assemblies the
/// router can route to.
/// </summary>
/// <remarks>
/// <para>This is the fixture Phase 6 shipped without, and its absence is why
/// nothing caught the client half silently never loading when the endpoints
/// went behind the fallback policy. The tests below run the real
/// <c>ApiClient</c> over a stub transport, so a change to the paths it fetches,
/// the credential it fetches them with, or what it does when one fails shows up
/// here rather than as an extension that quietly stops existing.</para>
///
/// <para><see cref="ExtensionBootstrapper.LoadAsync"/> taking an
/// <c>ApiClient</c> rather than a bare <c>HttpClient</c> is itself the guard
/// against the load drifting back off the authenticated path: there is no
/// unauthenticated client in the app to pass it.</para>
/// </remarks>
public class ExtensionBootstrapTests
{
    private const string Inventory = "api/extensions";

    /// <summary>
    /// Real bytes, so <c>Assembly.Load</c> is genuinely exercised. Any assembly
    /// on disk would do; this one is here because the test project already
    /// references it.
    /// </summary>
    private static readonly byte[] AssemblyBytes = File.ReadAllBytes(typeof(FixturePage).Assembly.Location);

    private const string AssemblyName = "Solo.Extension.Fixture.Client";

    [Fact]
    public async Task A_compatible_extension_is_downloaded_and_handed_to_the_router()
    {
        var http = new StubHttp()
            .RouteBytes($"{Inventory}/roadmap/client/{AssemblyName}.dll", AssemblyBytes)
            .RouteJson(Inventory, new[] { Descriptor("roadmap") });

        var catalog = new ExtensionCatalog();
        await ExtensionBootstrapper.LoadAsync(catalog, http.CreateClient());

        var loaded = Assert.Single(catalog.Loaded);
        Assert.Equal("roadmap", loaded.Descriptor.Id);
        Assert.Equal(AssemblyName, Assert.Single(catalog.RouteAssemblies).GetName().Name);
        Assert.Empty(catalog.Skipped);

        // And the nav entry travels with it, which is what puts the page in the
        // sidebar at all.
        Assert.Equal("/roadmap", Assert.Single(catalog.Nav).Route);
    }

    /// <summary>
    /// The catalog starts empty and gains its contents later, so the sidebar has
    /// to be told — it can be on screen well before the assemblies land.
    /// </summary>
    [Fact]
    public async Task Filling_the_catalog_raises_Changed()
    {
        var http = new StubHttp()
            .RouteBytes($"{Inventory}/roadmap/client/{AssemblyName}.dll", AssemblyBytes)
            .RouteJson(Inventory, new[] { Descriptor("roadmap") });

        var catalog = new ExtensionCatalog();
        var raised = 0;
        catalog.Changed += () => raised++;

        await ExtensionBootstrapper.LoadAsync(catalog, http.CreateClient());

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// <c>RouteAssemblies</c> is a <c>Router</c> parameter, read on every render
    /// of <c>App</c>. Projecting it afresh each time would have the router
    /// rebuild a route key for a list that has not changed.
    /// </summary>
    [Fact]
    public async Task RouteAssemblies_is_cached_between_reads()
    {
        var http = new StubHttp()
            .RouteBytes($"{Inventory}/roadmap/client/{AssemblyName}.dll", AssemblyBytes)
            .RouteJson(Inventory, new[] { Descriptor("roadmap") });

        var catalog = new ExtensionCatalog();
        Assert.Same(catalog.RouteAssemblies, catalog.RouteAssemblies);

        await ExtensionBootstrapper.LoadAsync(catalog, http.CreateClient());

        Assert.Same(catalog.RouteAssemblies, catalog.RouteAssemblies);
        Assert.Single(catalog.RouteAssemblies);
    }

    /// <summary>
    /// The Electron case the compat check exists for. Nothing is downloaded: the
    /// range is checked before any bytes move.
    /// </summary>
    [Fact]
    public async Task An_extension_needing_a_newer_client_is_skipped_without_downloading_it()
    {
        var http = new StubHttp().RouteJson(Inventory, new[]
        {
            Descriptor("roadmap", clientRange: $">={ContractVersion.Parsed.Major + 1}.0"),
        });

        var catalog = new ExtensionCatalog();
        await ExtensionBootstrapper.LoadAsync(catalog, http.CreateClient());

        Assert.Empty(catalog.Loaded);
        var skipped = Assert.Single(catalog.Skipped);
        Assert.Equal("roadmap", skipped.Id);
        Assert.Contains("Update the app", skipped.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(http.Paths, path => path.Contains("/client/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The fail-soft rule, and the only one that matters at load time: a
    /// skipped extension costs its own feature and nothing else.
    /// </summary>
    [Fact]
    public async Task One_extension_failing_to_download_does_not_take_the_others_with_it()
    {
        var http = new StubHttp()
            .Route($"{Inventory}/gone/client/{AssemblyName}.dll", HttpStatusCode.NotFound)
            .RouteBytes($"{Inventory}/roadmap/client/{AssemblyName}.dll", AssemblyBytes)
            .RouteJson(Inventory, new[] { Descriptor("gone"), Descriptor("roadmap") });

        var catalog = new ExtensionCatalog();
        await ExtensionBootstrapper.LoadAsync(catalog, http.CreateClient());

        Assert.Equal("roadmap", Assert.Single(catalog.Loaded).Descriptor.Id);
        Assert.Equal("gone", Assert.Single(catalog.Skipped).Id);
    }

    /// <summary>
    /// A server-only extension has no client half to fetch, and is neither
    /// loaded nor reported as a failure.
    /// </summary>
    [Fact]
    public async Task A_server_only_extension_is_neither_loaded_nor_skipped()
    {
        var http = new StubHttp().RouteJson(Inventory, new[]
        {
            Descriptor("hooks-only") with { ClientAssembly = null },
        });

        var catalog = new ExtensionCatalog();
        await ExtensionBootstrapper.LoadAsync(catalog, http.CreateClient());

        Assert.Empty(catalog.Loaded);
        Assert.Empty(catalog.Skipped);
    }

    /// <summary>
    /// The inventory is authenticated now, so a refused credential is a shape
    /// the loader has to survive — an app that cannot list extensions still runs
    /// without them.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refused_inventory_leaves_an_empty_catalog_rather_than_throwing(HttpStatusCode status)
    {
        var http = new StubHttp().Route(Inventory, status);

        var catalog = new ExtensionCatalog();
        await ExtensionBootstrapper.LoadAsync(catalog, http.CreateClient());

        Assert.Empty(catalog.Loaded);
        Assert.Empty(catalog.RouteAssemblies);
        Assert.Empty(catalog.Skipped);
    }

    [Fact]
    public async Task Separate_instances_keep_their_own_extension_catalogs()
    {
        var first = new StubHttp()
            .RouteBytes($"{Inventory}/roadmap/client/{AssemblyName}.dll", AssemblyBytes)
            .RouteJson(Inventory, new[] { Descriptor("roadmap") });
        var second = new StubHttp()
            .RouteBytes($"{Inventory}/design-docs/client/{AssemblyName}.dll", AssemblyBytes)
            .RouteJson(Inventory, new[] { Descriptor("design-docs"), Descriptor("future", ">=2.0") });
        var empty = new StubHttp().RouteJson(Inventory, Array.Empty<ClientExtensionDto>());
        var catalogs = new[] { new ExtensionCatalog(), new ExtensionCatalog(), new ExtensionCatalog() };

        await Task.WhenAll(
            ExtensionBootstrapper.LoadAsync(catalogs[0], first.CreateClient()),
            ExtensionBootstrapper.LoadAsync(catalogs[1], second.CreateClient()),
            ExtensionBootstrapper.LoadAsync(catalogs[2], empty.CreateClient()));

        Assert.Equal("roadmap", Assert.Single(catalogs[0].Loaded).Descriptor.Id);
        Assert.Equal("design-docs", Assert.Single(catalogs[1].Loaded).Descriptor.Id);
        Assert.Equal("future", Assert.Single(catalogs[1].Skipped).Id);
        Assert.Empty(catalogs[0].Skipped);
        Assert.Empty(catalogs[2].Loaded);
        Assert.Empty(catalogs[2].Skipped);
        Assert.DoesNotContain(first.Paths, p => p.Contains("design-docs", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Paths, p => p.Contains("roadmap", StringComparison.Ordinal));
    }

    private static ClientExtensionDto Descriptor(string id, string clientRange = ">=1.0 <2.0") =>
        new(id, id, "1.0.0", clientRange, AssemblyName,
            [new ClientExtensionNavDto($"/{id}", id, null, "Read")]);
}
