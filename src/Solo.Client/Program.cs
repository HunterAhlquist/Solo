using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client;
using Solo.Client.Api;
using Solo.Client.Auth;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Shared.Dtos;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ---------------------------------------------------------------------------
// Instance address. The hosted web build is same-origin; an Electron shell
// would point this at a self-hosted server instead. Everything downstream —
// REST, the hub, the config fetch — resolves URLs through it.
// ---------------------------------------------------------------------------
var instance = new InstanceConnection(builder.HostEnvironment.BaseAddress);

// ---------------------------------------------------------------------------
// Anonymous bootstrap. Main is async precisely so the OIDC provider can be
// configured from the API's environment rather than baked into the client.
// ---------------------------------------------------------------------------
AppConfig config;
using(var bootstrapServices=builder.Services.BuildServiceProvider())
{
    config=await LoadConfigAsync(instance,bootstrapServices.GetRequiredService<IJSRuntime>());
}

// ---------------------------------------------------------------------------
// HTTP, realtime, state and the (empty) extension catalog — see
// SoloClientServices for the lifetimes. Extensions are deliberately not loaded
// here any more: the endpoints serving them are authenticated, and the
// credential comes from an authentication provider that does not exist until
// the host below is built. App.razor loads them on the first authenticated
// state it sees and re-renders, which is what makes the router re-scan routes.
// ---------------------------------------------------------------------------
builder.Services.AddSoloClient(instance, config);

// ---------------------------------------------------------------------------
// Authentication. Every path ends in the same place — an ID token on the wire —
// but Google can't reach it the same way as everyone else.
// ---------------------------------------------------------------------------
if (config.IsGoogle)
{
    // Google's "Web application" clients require a client_secret at the token
    // endpoint even with PKCE, and a browser cannot hold a secret. Google Identity
    // Services skips the token endpoint entirely and returns the ID token itself.
    builder.Services.AddAuthorizationCore();
    builder.Services.AddScoped<AuthenticationStateProvider, GoogleAuthenticationStateProvider>();
}
else if (config.UsesOidcRedirect)
{
    builder.Services.AddOidcAuthentication(options =>
    {
        options.ProviderOptions.Authority = config.Auth!.Authority;
        options.ProviderOptions.ClientId = config.Auth.ClientId;
        options.ProviderOptions.ResponseType = "code"; // code + PKCE
        options.ProviderOptions.DefaultScopes.Clear();
        options.ProviderOptions.DefaultScopes.Add("openid");
        options.ProviderOptions.DefaultScopes.Add("profile");
        options.ProviderOptions.DefaultScopes.Add("email");
        options.UserOptions.NameClaim = "name";
    });
}
else
{
    // No provider configured yet: keep the app bootable so /setup can say so.
    builder.Services.AddAuthorizationCore();
    builder.Services.AddScoped<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();
}

// ---------------------------------------------------------------------------
// MudBlazor. Compact density and a short snackbar: this is a tool.
// ---------------------------------------------------------------------------
builder.Services.AddMudServices(options =>
{
    options.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    options.SnackbarConfiguration.PreventDuplicates = true;
    options.SnackbarConfiguration.NewestOnTop = true;
    options.SnackbarConfiguration.ShowCloseIcon = true;
    options.SnackbarConfiguration.VisibleStateDuration = 4000;
    options.SnackbarConfiguration.HideTransitionDuration = 200;
});
builder.Services.AddMudMarkdownServices();

var host = builder.Build();

// In the desktop shell the client is served from a local origin that proxies the
// API onward, so the address a user must paste into a tool running outside the
// app is the instance the shell points at — not this page's origin. Asked once,
// here, because the answer cannot change without a restart and no page should
// have to know the shell exists. A no-op on the web build.
await host.Services.GetRequiredService<InstanceConnection>()
    .ResolveExternalAddressAsync(host.Services.GetRequiredService<IJSRuntime>());

// The boot splash and the tab title are plain HTML, on screen before the API
// has said what this instance is called. Leave the name behind for the next
// boot's js/theme-boot.js, the same way the theme leaves its colours.
if (config.LoadError is null)
{
    try
    {
        await host.Services.GetRequiredService<IJSRuntime>()
            .InvokeVoidAsync("solo.branding.remember", config.HasOwnName ? config.SiteName : null);
    }
    catch (JSException)
    {
        // Cosmetic only — the next boot says "Solo" until the app has loaded.
    }
}

await host.RunAsync();

// ---------------------------------------------------------------------------
static async Task<AppConfig> LoadConfigAsync(InstanceConnection instance,IJSRuntime js)
{
    using var http = new HttpClient(new Solo.Client.Offline.OfflineCacheHandler(new(js)){InnerHandler=new HttpClientHandler()}) { BaseAddress = instance.BaseAddress,Timeout=TimeSpan.FromSeconds(15) };
    try
    {
        var api = new ApiClient(http, instance);

        // Both calls are anonymous and independent, so they overlap rather than
        // adding a second round trip to a startup that already blocks on one.
        var authTask = api.GetAuthConfigAsync();
        var themeTask = LoadThemeAsync(api);

        var auth = await authTask;
        return new AppConfig { Auth = auth, Theme = await themeTask, SetupCompleted = auth.SetupCompleted };
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ApiException)
    {
        // The instance is unreachable or still starting. App.razor renders a
        // dead-end error screen rather than a half-configured router.
        return new AppConfig { LoadError = e.Message };
    }
}

/// <summary>
/// Branding is not worth failing a boot over: an instance that answers the auth
/// config but not this still starts, on <c>MudThemeFactory.Fallback</c>.
/// </summary>
static async Task<ThemeDto?> LoadThemeAsync(ApiClient api)
{
    try
    {
        return await api.GetActiveThemeAsync();
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ApiException)
    {
        return null;
    }
}
