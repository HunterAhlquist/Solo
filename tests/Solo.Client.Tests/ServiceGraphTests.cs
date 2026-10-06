using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.Client;
using Solo.Client.Api;
using Solo.Client.Auth;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// Blazor WASM runs <c>ValidateOnBuild</c>/<c>ValidateScopes</c> only in the
/// Development environment, so a lifetime mistake sails through a Release publish
/// and then blanks the page the moment you run <c>dotnet watch</c>. This builds
/// the container the strict way, every time.
/// </summary>
public class ServiceGraphTests
{
    private static ServiceCollection SoloServices(string provider = "microsoft")
    {
        var services = new ServiceCollection();
        services.AddSoloClient(
            new InstanceConnection("https://solo.test/"),
            new AppConfig
            {
                Auth = new AuthConfigDto("https://issuer.test", "cid", provider, true),
                SetupCompleted = true,
            });

        // Supplied by the Blazor host and MudBlazor in the real app.
        services.AddSingleton<IJSRuntime, StubJsRuntime>();
        services.AddSingleton<NavigationManager, StubNavigationManager>();
        services.AddSingleton<ISnackbar, StubSnackbar>();
        services.AddScoped<IDialogService, DialogService>();
        return services;
    }

    [Fact]
    public void The_service_graph_builds_under_the_strict_validation_Development_uses()
    {
        using var provider = SoloServices().BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.NotNull(provider);
    }

    [Fact]
    public void No_singleton_captures_a_scoped_dependency()
    {
        // The specific failure that shipped: BoardHubClient was registered as a
        // singleton while depending on the scoped IdTokenProvider.
        var services = SoloServices();
        var scoped = services
            .Where(d => d.Lifetime == ServiceLifetime.Scoped)
            .Select(d => d.ServiceType)
            .ToHashSet();

        var offenders = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton && d.ImplementationType is not null)
            .SelectMany(d => d.ImplementationType!
                .GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Where(p => scoped.Contains(p.ParameterType))
                .Select(p => $"{d.ImplementationType!.Name} -> {p.ParameterType.Name}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task The_hub_client_and_its_interface_resolve_to_one_instance()
    {
        // BoardHubClient and BoardStore are async-disposable, so the scope must be
        // torn down asynchronously.
        await using var provider = SoloServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Assert.Same(
            scope.ServiceProvider.GetRequiredService<BoardHubClient>(),
            scope.ServiceProvider.GetRequiredService<IBoardHubClient>());
    }

    [Fact]
    public async Task Every_service_a_component_injects_can_actually_be_resolved()
    {
        await using var provider = SoloServices().BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
        await using var scope = provider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ApiClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SetupClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<BoardStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<UserSession>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SaveGuard>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IBoardHubClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GoogleIdentityService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TaskDrawerService>());
        Assert.Same(
            scope.ServiceProvider.GetRequiredService<TaskDrawerService>(),
            scope.ServiceProvider.GetRequiredService<Solo.Shared.Extensibility.ISoloTaskDrawer>());
        Assert.IsType<TaskPickerService>(
            scope.ServiceProvider.GetRequiredService<Solo.Shared.Extensibility.ISoloTaskPicker>());
        Assert.Same(
            scope.ServiceProvider.GetRequiredService<ContextMenuService>(),
            scope.ServiceProvider.GetRequiredService<Solo.Shared.Extensibility.ISoloContextMenu>());
    }

    [Theory]
    [InlineData("google")]
    [InlineData("microsoft")]
    [InlineData("generic")]
    public void The_graph_validates_for_every_provider(string provider)
    {
        using var provider_ = SoloServices(provider).BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.NotNull(provider_);
    }

    private sealed class StubJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager() => Initialize("https://solo.test/", "https://solo.test/");
    }

    private sealed class StubSnackbar : ISnackbar
    {
        public SnackbarConfiguration Configuration { get; } = new();

        public IEnumerable<Snackbar> ShownSnackbars => [];

        public event Action? OnSnackbarsUpdated
        {
            add { }
            remove { }
        }

        public Snackbar? Add(string message, Severity severity = Severity.Normal,
            Action<SnackbarOptions>? configure = null, string? key = "") => null;

        public Snackbar? Add(MarkupString message, Severity severity = Severity.Normal,
            Action<SnackbarOptions>? configure = null, string? key = "") => null;

        public Snackbar? Add(RenderFragment message, Severity severity = Severity.Normal,
            Action<SnackbarOptions>? configure = null, string? key = "") => null;

        public Snackbar? Add<T>(Dictionary<string, object>? componentParameters = null,
            Severity severity = Severity.Normal, Action<SnackbarOptions>? configure = null,
            string? key = "") where T : IComponent => null;

        public void Clear()
        {
        }

        public void Remove(Snackbar snackbar)
        {
        }

        public void RemoveByKey(string key)
        {
        }

        public void Dispose()
        {
        }
    }
}
