using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Components;
using Solo.Client.Realtime;
using Solo.Client.State;
using Solo.Client.Theming;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Client.Testing;

/// <summary>
/// The bUnit plumbing every client test needs, MudBlazor or not: components
/// chatter to JS on render and we're asserting markup, so interop runs loose; and
/// several services are async-disposable, which the synchronous xunit teardown
/// can't handle — hence <see cref="IAsyncLifetime"/>.
/// </summary>
public abstract class SoloBunitContext : BunitContext, IAsyncLifetime
{
    protected SoloBunitContext() => JSInterop.Mode = JSRuntimeMode.Loose;

    /// <summary>
    /// <see cref="BunitContext.DisposeComponentsAsync"/>, called from the
    /// renderer's dispatcher. bUnit clears its list of roots straight away but
    /// detaches them in a dispatcher callback; when a component still has async
    /// work running there, that callback queues, finds the list already empty,
    /// and nothing is disposed. From the dispatcher it runs inline instead.
    /// </summary>
    protected Task DisposeComponentsOnDispatcherAsync() => Renderer.Dispatcher.InvokeAsync(DisposeComponentsAsync);

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();
}

/// <summary>
/// Renders MudBlazor components in isolation, adding the services they expect:
/// popovers otherwise assert that a <c>MudPopoverProvider</c> is on the page, and
/// there is no layout here.
/// </summary>
public abstract class ComponentTestBase : SoloBunitContext
{
    protected ComponentTestBase()
    {
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddMudMarkdownServices();

        // Both layouts and the sign-in page inject it. No theme is set, so
        // components see the built-in fallback — which is what a test asserting
        // markup wants anyway.
        Services.AddScoped(sp => new ThemeState(sp.GetRequiredService<IJSRuntime>()));

        // Every component that saves injects it, so it belongs here rather than
        // in each test class that happens to render one.
        Services.AddScoped<SaveGuard>();

        // MainLayout hosts the context menu, closed unless something opens it,
        // so every test that renders the layout needs it.
        Services.AddScoped<ContextMenuService>();

        // The layout, a board and its drawer all look for extension
        // contributions. Empty, as an instance with none installed; a test about
        // extensions registers its own catalog after this, which wins.
        Services.AddSingleton<Solo.Client.Extensions.ExtensionCatalog>();
        Services.AddScoped<TaskMenuService>();
    }
}
