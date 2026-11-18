using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Hosting;
using Solo.Client.Layout;
using Solo.Client.Theming;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The theme's picture behind the app (<see cref="ThemeDto.Background"/>): there
/// when the theme has one, in the scheme's own version, and left to the app when
/// the client is shown inside one.
/// </summary>
public sealed class AppBackdropTests : SoloBunitContext
{
    private static readonly ThemeDto Pictured = new()
    {
        Id = "pictured",
        Name = "Pictured",
        Background = new ThemeBackgroundDto
        {
            Image = "api/themes/pictured/abc/content/dark.webp",
            ImageLight = "api/themes/pictured/abc/content/light.webp",
            Fit = ThemeBackgroundFit.Cover,
            Position = "center",
            Scrim = 0.25,
        },
    };

    private async Task<IRenderedComponent<AppBackdrop>> Backdrop(ThemeDto theme, bool dark = true, bool hosted = false)
    {
        var state = new ThemeState(JSInterop.JSRuntime);
        state.SetTheme(theme);
        await state.SetPreferenceAsync(dark ? ColorSchemePreference.Dark : ColorSchemePreference.Light);
        Services.AddSingleton(state);
        Services.AddSingleton(new HostShell(hosted));
        return Render<AppBackdrop>();
    }

    [Fact]
    public async Task A_theme_with_a_background_draws_it_behind_the_app()
    {
        var backdrop = await Backdrop(Pictured);

        var style = backdrop.Find(".app-backdrop").GetAttribute("style")!;
        Assert.Contains("--backdrop-image:url('http://localhost/api/themes/pictured/abc/content/dark.webp')", style);
        Assert.Contains("--backdrop-size:cover", style);
        Assert.Contains("--backdrop-position:center", style);
        Assert.Contains("--backdrop-scrim:0.25", style);
        Assert.Equal("true", backdrop.Find(".app-backdrop").GetAttribute("aria-hidden"));
    }

    [Fact]
    public async Task The_light_scheme_gets_the_light_picture_and_follows_a_change()
    {
        var backdrop = await Backdrop(Pictured, dark: false);
        Assert.Contains("light.webp", backdrop.Find(".app-backdrop").GetAttribute("style"));

        await Services.GetRequiredService<ThemeState>().SetPreferenceAsync(ColorSchemePreference.Dark);

        backdrop.WaitForAssertion(() =>
            Assert.Contains("dark.webp", backdrop.Find(".app-backdrop").GetAttribute("style")));
    }

    [Fact]
    public async Task A_theme_without_one_draws_nothing()
    {
        var backdrop = await Backdrop(new ThemeDto { Id = "plain", Name = "Plain" });

        Assert.Empty(backdrop.FindAll(".app-backdrop"));
    }

    [Fact]
    public async Task Inside_an_app_that_hosts_the_client_the_picture_is_the_apps_to_draw()
    {
        var backdrop = await Backdrop(Pictured, hosted: true);

        Assert.Empty(backdrop.FindAll(".app-backdrop"));
    }
}
