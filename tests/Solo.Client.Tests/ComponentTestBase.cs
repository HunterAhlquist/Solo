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

namespace Solo.Client.Tests;

/// <summary>
/// The bUnit plumbing every client test needs, MudBlazor or not: components
/// chatter to JS on render and we're asserting markup, so interop runs loose; and
/// several services are async-disposable, which the synchronous xunit teardown
/// can't handle — hence <see cref="IAsyncLifetime"/>.
/// </summary>
public abstract class SoloBunitContext : BunitContext, IAsyncLifetime
{
    protected SoloBunitContext() => JSInterop.Mode = JSRuntimeMode.Loose;

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
    }
}
