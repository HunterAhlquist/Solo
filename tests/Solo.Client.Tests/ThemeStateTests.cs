using Bunit;
using Solo.Client.Theming;

namespace Solo.Client.Tests;

public sealed class ThemeStateTests : BunitContext, IAsyncLifetime
{
    public ThemeStateTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    [Fact]
    public async Task Preference_changes_update_the_desktop_appearance_source()
    {
        var theme = new ThemeState(JSInterop.JSRuntime);

        await theme.SetPreferenceAsync(ColorSchemePreference.Light);
        await theme.SetPreferenceAsync(ColorSchemePreference.Dark);
        await theme.SetPreferenceAsync(ColorSchemePreference.System);

        var sources = JSInterop.Invocations
            .Where(invocation => invocation.Identifier == "solo.theme.setSource")
            .Select(invocation => invocation.Arguments[0]?.ToString() ?? string.Empty)
            .ToArray();
        Assert.Equal(["light", "dark", "system"], sources);
    }

    [Fact]
    public async Task System_changes_update_the_document_color_scheme()
    {
        var theme = new ThemeState(JSInterop.JSRuntime);

        await theme.SetSystemPreferenceAsync(prefersDark: false);

        var invocation = Assert.Single(JSInterop.Invocations,
            candidate => candidate.Identifier == "solo.theme.setResolvedScheme");
        Assert.Equal(false, invocation.Arguments[0]);
    }
}
