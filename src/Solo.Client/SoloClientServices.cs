using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Api;
using Solo.Client.Auth;
using Solo.Client.Realtime;
using Solo.Client.State;

namespace Solo.Client;

/// <summary>
/// Solo's own service graph, apart from MudBlazor and the OIDC provider.
/// Extracted from <c>Program.cs</c> so a test can build it under
/// <c>ValidateOnBuild</c>/<c>ValidateScopes</c> — Blazor WASM only runs those
/// checks in Development, and a lifetime mistake there is a blank page on boot.
/// </summary>
public static class SoloClientServices
{
    public static IServiceCollection AddSoloClient(
        this IServiceCollection services, InstanceConnection instance, AppConfig config)
    {
        services.AddSingleton(instance);
        services.AddSingleton(config);

        // HTTP: one authenticated client for the API, one bare client for the
        // setup scheme (static credentials, not a bearer token).
        services.AddSingleton<ConnectionIdAccessor>();
        services.AddScoped<GoogleIdentityService>();
        services.AddScoped<IdTokenProvider>();
        services.AddScoped<IdTokenMessageHandler>();

        services.AddHttpClient<ApiClient>(client => client.BaseAddress = instance.BaseAddress)
            .AddHttpMessageHandler<IdTokenMessageHandler>();
        services.AddHttpClient<SetupClient>(client => client.BaseAddress = instance.BaseAddress);

        // Realtime + state. BoardHubClient is scoped rather than singleton: it
        // depends on the scoped IdTokenProvider, and WASM's single root scope
        // already makes "scoped" mean one instance for the app's lifetime.
        services.AddScoped<BoardHubClient>();
        services.AddScoped<IBoardHubClient>(sp => sp.GetRequiredService<BoardHubClient>());
        services.AddScoped<BoardStore>();
        services.AddScoped<ProjectNavStore>();
        services.AddScoped<UserSession>();
        services.AddScoped<ThemeService>();
        services.AddScoped<SaveGuard>();

        return services;
    }
}
