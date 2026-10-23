using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Solo.Client.Api;
using Solo.Client.Auth;
using Solo.Client.Extensions;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Client.Theming;
using Solo.Shared.Extensibility;

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
        // The clock DayRollover reads "today" from, so a test can move it.
        services.AddSingleton(TimeProvider.System);

        // Empty at boot. The endpoints serving extension client halves are
        // authenticated, so App.razor fills this once there is a signed-in user
        // and re-renders to let the router pick up their routes.
        services.AddSingleton<ExtensionCatalog>();

        // HTTP: one authenticated client for the API, one bare client for the
        // setup scheme (static credentials, not a bearer token).
        services.AddSingleton<ConnectionIdAccessor>();
        services.AddSingleton<SessionExpiry>();
        services.AddScoped<GoogleIdentityService>();
        services.AddScoped<IdTokenProvider>();
        services.AddScoped<IdTokenMessageHandler>();

        services.AddHttpClient<ApiClient>(client => client.BaseAddress = instance.BaseAddress)
            .AddHttpMessageHandler<IdTokenMessageHandler>();
        // What an extension's client assembly injects to call its own endpoints.
        // Forwarded to ApiClient rather than given its own HttpClient, so an
        // extension shares the app's credential and cannot authenticate differently.
        services.AddScoped<ISoloClientApi>(sp => sp.GetRequiredService<ApiClient>());
        services.AddHttpClient<SetupClient>(client => client.BaseAddress = instance.BaseAddress);

        // Realtime + state. BoardHubClient is scoped rather than singleton: it
        // mints its hub tickets through ApiClient, whose handler depends on the
        // scoped IdTokenProvider, and WASM's single root scope already makes
        // "scoped" mean one instance for the app's lifetime.
        services.AddScoped<BoardHubClient>();
        services.AddScoped<IBoardHubClient>(sp => sp.GetRequiredService<BoardHubClient>());
        // Extensions' live topics ride the same connection, so a page with both a
        // board and an extension's live view holds one socket, not two.
        services.AddScoped<LiveTopics>();
        services.AddScoped<ISoloLiveTopics>(sp => sp.GetRequiredService<LiveTopics>());
        services.AddScoped<BoardStore>();
        services.AddScoped<ProjectNavStore>();
        services.AddScoped<UserSession>();
        services.AddScoped<RecentProjectsStore>();
        services.AddScoped<ProjectOrderStore>();
        services.AddScoped<SaveGuard>();
        services.AddScoped<DragBridge>();
        // The task drawer, opened over extension pages. Extension client halves
        // inject the interface; they can't register services of their own.
        services.AddScoped<TaskDrawerService>();
        services.AddScoped<ISoloTaskDrawer>(sp => sp.GetRequiredService<TaskDrawerService>());
        // The task picker, for the same reason: its dialog is core's to draw.
        services.AddScoped<ISoloTaskPicker, TaskPickerService>();
        services.AddScoped<WorkspaceState>();
        services.AddScoped<ISoloWorkspace>(sp => sp.GetRequiredService<WorkspaceState>());
        // The context menu: core's to draw, and only core can ask the desktop
        // shell for a native one. ContextMenuHost renders what this holds.
        services.AddScoped<ContextMenuService>();
        services.AddScoped<ISoloContextMenu>(sp => sp.GetRequiredService<ContextMenuService>());
        // Core's task menu, which extensions add to through contributor classes
        // it finds in their assemblies and creates on demand.
        services.AddScoped<TaskMenuService>();

        // The site theme arrives with the bootstrap config; the colour scheme is
        // read from localStorage on first render (see SoloThemeProvider).
        services.AddScoped(sp =>
        {
            var state = new ThemeState(sp.GetRequiredService<IJSRuntime>());
            state.SetTheme(config.Theme);
            return state;
        });

        return services;
    }
}
