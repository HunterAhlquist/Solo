using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
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
var config = await LoadConfigAsync(instance);

// ---------------------------------------------------------------------------
// HTTP, realtime and state (see SoloClientServices for the lifetimes).
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

await builder.Build().RunAsync();

// ---------------------------------------------------------------------------
static async Task<AppConfig> LoadConfigAsync(InstanceConnection instance)
{
    using var http = new HttpClient { BaseAddress = instance.BaseAddress };
    try
    {
        var auth = await new ApiClient(http).GetAuthConfigAsync();
        return new AppConfig { Auth = auth, SetupCompleted = auth.SetupCompleted };
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ApiException)
    {
        // The instance is unreachable or still starting. App.razor renders a
        // dead-end error screen rather than a half-configured router.
        return new AppConfig { LoadError = e.Message };
    }
}
