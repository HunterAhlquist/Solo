using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client;
using Solo.Client.Api;
using Solo.Client.Auth;
using Solo.Client.Extensions;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Client.Theming;
using Solo.Mcp.Client;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// Where the extension load is wired: <c>App</c> fetches the client halves once
/// there is an authenticated user, and re-renders so the router picks up their
/// routes.
/// </summary>
/// <remarks>
/// The endpoints behind this are authenticated, so the load cannot happen at
/// boot the way it used to — and an app that fetched them too early would show
/// exactly the symptom that made them anonymous in the first place: every
/// extension client half silently missing. These tests pin both halves of that,
/// the "not before sign-in" and the "and then it works".
/// </remarks>
public class AppExtensionLoadTests : SoloBunitContext
{
    private const string Inventory = "api/extensions";
    private const string AssemblyName = "Solo.Mcp.Client";

    /// <summary>
    /// The real MCP client half, as bytes — what the instance would serve. The
    /// loader <c>Assembly.Load</c>s these, so the router sees a route that this
    /// test project's own references could not have given it.
    /// </summary>
    private static readonly byte[] AssemblyBytes = File.ReadAllBytes(typeof(McpApi).Assembly.Location);

    private readonly StubHttp _http = new();
    private readonly ExtensionCatalog _catalog = new();

    public AppExtensionLoadTests()
    {
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddMudMarkdownServices();

        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton<ISoloClientApi>(api);
        Services.AddSingleton(_catalog);
        Services.AddSingleton(new AppConfig { SetupCompleted = true });
        Services.AddSingleton<SessionExpiry>();
        Services.AddSingleton(new UserSession(api));
        Services.AddSingleton(new ProjectNavStore(api));
        Services.AddScoped<GoogleIdentityService>();
        Services.AddScoped<RecentProjectsStore>();
        Services.AddScoped<ProjectOrderStore>();
        Services.AddScoped<DragBridge>();
        Services.AddSingleton<IBoardHubClient>(new FakeBoardHub());
        Services.AddScoped<SaveGuard>();
        Services.AddScoped(sp => new ThemeState(sp.GetRequiredService<IJSRuntime>()));

        _http.RouteBytes($"{Inventory}/mcp/client/{AssemblyName}.dll", AssemblyBytes)
             .RouteJson(Inventory, new[]
             {
                 new ClientExtensionDto("mcp", "MCP", "1.0.0", ">=1.0 <2.0", AssemblyName,
                     [new ClientExtensionNavDto("/mcp", "MCP access", null, "Read")]),
             });
    }

    /// <summary>
    /// Nothing is asked for before there is a credential to ask with — that
    /// request would 401, and 401ing on every boot is how the endpoints ended up
    /// anonymous the first time.
    /// </summary>
    [Fact]
    public void A_signed_out_visitor_fetches_no_extensions()
    {
        this.AddAuthorization();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("mcp");

        Render<App>();

        Assert.DoesNotContain(_http.Paths, path => path.Contains(Inventory, StringComparison.Ordinal));
        Assert.Empty(_catalog.Loaded);
    }

    /// <summary>
    /// A deep link to an extension page while signed out, end to end. Signed
    /// out, the router has no entry for the route — which is not the same as the
    /// page not existing, so the visitor is sent to sign in with the route kept
    /// rather than told it is missing, and lands on it afterwards.
    /// </summary>
    [Fact]
    public void A_deep_link_to_an_extension_page_survives_the_sign_in_round_trip()
    {
        var authorization = this.AddAuthorization();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("mcp");

        var app = Render<App>();

        app.WaitForAssertion(() =>
            Assert.EndsWith("signin?returnUrl=%2Fmcp", navigation.Uri, StringComparison.Ordinal));

        // Signing in, as GoogleSignInButton does it: the authentication state
        // changes, and the sign-in page then navigates to the returnUrl it was
        // handed. The instance is held mid-answer so the route cannot resolve
        // on some other component's redraw — only the load settling can do it.
        var instanceAnswering = new TaskCompletionSource();
        _http.Pause = () => instanceAnswering.Task;

        authorization.SetAuthorized("Hunter");
        navigation.NavigateTo("mcp");

        // Waiting, not bounced back to the sign-in page it just came from, and
        // not told the page does not exist.
        Assert.EndsWith("/mcp", navigation.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain("That page doesn't exist", app.Markup, StringComparison.Ordinal);
        Assert.Empty(app.FindAll("h1"));

        instanceAnswering.SetResult();

        app.WaitForAssertion(() => Assert.Equal("MCP access", app.Find("h1").TextContent));
    }

    /// <summary>
    /// The whole point: once there is a user, the client half is fetched, loaded
    /// and handed to the router, and its nav entry reaches the sidebar.
    /// </summary>
    [Fact]
    public void An_authenticated_user_gets_the_client_half_and_its_route()
    {
        this.AddAuthorization().SetAuthorized("Hunter");

        var app = Render<App>();

        app.WaitForAssertion(() =>
        {
            Assert.Equal(AssemblyName, Assert.Single(_catalog.RouteAssemblies).GetName().Name);
            Assert.Equal("/mcp", Assert.Single(_catalog.InstanceNav).Route);
        });

        // The bytes came off the wire rather than out of this project's own
        // reference to Solo.Mcp.Client.
        Assert.Contains(_http.Paths, path =>
            path.Contains($"{Inventory}/mcp/client/{AssemblyName}.dll", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the route the assembly carries actually resolves — the step the
    /// re-render exists for. A router that never re-scanned would still be
    /// rendering "that page doesn't exist" here.
    /// </summary>
    [Fact]
    public void The_extension_route_resolves_once_the_assembly_is_in()
    {
        this.AddAuthorization().SetAuthorized("Hunter");
        Services.GetRequiredService<NavigationManager>().NavigateTo("mcp");

        var app = Render<App>();

        // The page's own heading, not the sidebar entry of the same name: this
        // has to prove the route resolved, not that the nav arrived.
        app.WaitForAssertion(() => Assert.Equal("MCP access", app.Find("h1").TextContent));
        Assert.DoesNotContain("That page doesn't exist", app.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A route no assembly claims is still a 404 once the load has settled —
    /// the waiting state must not swallow genuine typos.
    /// </summary>
    [Fact]
    public void An_unknown_route_is_still_missing_once_extensions_have_loaded()
    {
        this.AddAuthorization().SetAuthorized("Hunter");
        Services.GetRequiredService<NavigationManager>().NavigateTo("no-such-page");

        var app = Render<App>();

        app.WaitForAssertion(() =>
            Assert.Contains("That page doesn't exist", app.Markup, StringComparison.Ordinal));
    }
}
