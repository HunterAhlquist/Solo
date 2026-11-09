using Bunit;
using Solo.Client.Theming;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

public sealed class ThemeStateTests : SoloBunitContext
{
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

    [Fact]
    public async Task The_scheme_carries_the_app_bar_colour_without_alpha()
    {
        var theme = new ThemeState(JSInterop.JSRuntime);
        theme.SetTheme(new ThemeDto { Light = new ThemePaletteDto { AppbarBackground = "#12345680" } });

        await theme.SetPreferenceAsync(ColorSchemePreference.Light);

        var invocation = Assert.Single(JSInterop.Invocations,
            candidate => candidate.Identifier == "solo.theme.setResolvedScheme");
        Assert.Equal("#123456", invocation.Arguments[1]);
    }
}
