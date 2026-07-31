using Solo.Api.Theming;
using Solo.Shared.Dtos;

namespace Solo.Api.Tests;

/// <summary>
/// Every theme shipped in the image, in both schemes, has to be readable.
///
/// <para>This exists because the light scheme was unusable for a long time and
/// nothing caught it: it was defined, never rendered, and quietly drifted. A
/// palette is a table of numbers, so the check is a test rather than a review
/// habit — and it applies to the two new themes and to whatever is added next.</para>
/// </summary>
public class ThemeContrastTests
{
    /// <summary>WCAG AA for body text.</summary>
    private const double BodyText = 4.5;

    /// <summary>WCAG AA for large text and for the boundaries of UI components.</summary>
    private const double LargeText = 3.0;

    public static TheoryData<string, bool> Themes()
    {
        var data = new TheoryData<string, bool>();
        foreach (var theme in Load())
        {
            data.Add(theme.Id, true);
            data.Add(theme.Id, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Text_is_readable_on_the_surfaces_it_sits_on(string themeId, bool dark)
    {
        var palette = Palette(themeId, dark);

        foreach (var surface in Surfaces(palette))
        {
            AssertContrast(palette.TextPrimary, surface, BodyText, $"{themeId}/{Scheme(dark)}: text-primary");
            AssertContrast(palette.TextSecondary, surface, BodyText, $"{themeId}/{Scheme(dark)}: text-secondary");
        }
    }

    /// <summary>
    /// The accents double as link and icon colours on the page background, so
    /// they have to clear the large-text bar there — this is exactly what a
    /// bright yellow fails on a white page.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void Accents_are_visible_against_the_page(string themeId, bool dark)
    {
        var palette = Palette(themeId, dark);

        foreach (var surface in Surfaces(palette))
        {
            AssertContrast(palette.Primary, surface, LargeText, $"{themeId}/{Scheme(dark)}: primary");
            AssertContrast(palette.Secondary, surface, LargeText, $"{themeId}/{Scheme(dark)}: secondary");
            AssertContrast(palette.Error, surface, LargeText, $"{themeId}/{Scheme(dark)}: error");
            AssertContrast(palette.Success, surface, LargeText, $"{themeId}/{Scheme(dark)}: success");
            AssertContrast(palette.Warning, surface, LargeText, $"{themeId}/{Scheme(dark)}: warning");
            AssertContrast(palette.Info, surface, LargeText, $"{themeId}/{Scheme(dark)}: info");
        }
    }

    /// <summary>
    /// Filled buttons and snackbars draw their label in the fill's
    /// <c>*ContrastText</c>. Those are derived at import time, so this checks the
    /// derivation actually lands on a readable answer for real palettes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void Filled_controls_have_readable_labels(string themeId, bool dark)
    {
        var palette = Palette(themeId, dark);

        AssertContrast(palette.PrimaryContrastText, palette.Primary, BodyText, $"{themeId}/{Scheme(dark)}: on primary");
        AssertContrast(palette.SecondaryContrastText, palette.Secondary, BodyText, $"{themeId}/{Scheme(dark)}: on secondary");
        AssertContrast(palette.SuccessContrastText, palette.Success, BodyText, $"{themeId}/{Scheme(dark)}: on success");
        AssertContrast(palette.WarningContrastText, palette.Warning, BodyText, $"{themeId}/{Scheme(dark)}: on warning");
        AssertContrast(palette.ErrorContrastText, palette.Error, BodyText, $"{themeId}/{Scheme(dark)}: on error");
        AssertContrast(palette.InfoContrastText, palette.Info, BodyText, $"{themeId}/{Scheme(dark)}: on info");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_app_bar_reads_against_its_own_background(string themeId, bool dark)
    {
        var palette = Palette(themeId, dark);

        AssertContrast(palette.AppbarText, palette.AppbarBackground, LargeText, $"{themeId}/{Scheme(dark)}: app bar");
        AssertContrast(palette.DrawerText, palette.DrawerBackground, BodyText, $"{themeId}/{Scheme(dark)}: drawer text");
        AssertContrast(palette.DrawerIcon, palette.DrawerBackground, LargeText, $"{themeId}/{Scheme(dark)}: drawer icon");
    }

    /// <summary>Both schemes must be defined, or switching lands on MudBlazor's defaults.</summary>
    [Fact]
    public void Every_built_in_theme_defines_both_schemes()
    {
        foreach (var theme in Load())
        {
            Assert.NotNull(theme.Manifest.Dark);
            Assert.NotNull(theme.Manifest.Light);
        }
    }

    private static void AssertContrast(string? foreground, string? background, double minimum, string what)
    {
        // A colour the theme leaves unset falls through to MudBlazor's default,
        // which isn't this theme's business to guarantee.
        if (foreground is null || background is null)
        {
            return;
        }

        var ratio = ThemeColors.ContrastRatio(foreground, background);

        // Translucent hairline colours (#rrggbbaa) composite over something this
        // can't know; they carry no text and are excluded rather than guessed at.
        if (ratio == 0)
        {
            return;
        }

        Assert.True(
            ratio >= minimum,
            $"{what}: {foreground} on {background} is {ratio:F2}:1, below the {minimum:F1}:1 minimum.");
    }

    private static IEnumerable<string?> Surfaces(ThemePaletteDto palette) =>
        [palette.Background, palette.Surface, palette.BackgroundGray];

    private static ThemePaletteDto Palette(string themeId, bool dark)
    {
        var theme = Load().Single(t => t.Id == themeId).Manifest;
        return (dark ? theme.Dark : theme.Light)!;
    }

    private static string Scheme(bool dark) => dark ? "dark" : "light";

    private static IReadOnlyList<ThemePackage> Load()
    {
        var root = FindThemesDirectory();
        return [.. Directory.EnumerateDirectories(root)
            .Select(d => ThemePackageLoader.LoadDirectory(d, isBuiltIn: true))];
    }

    /// <summary>
    /// The built-in theme sources, walked up to from the test binary — reading
    /// the repo copy rather than a build output keeps the assertion pointed at
    /// what a reviewer edits.
    /// </summary>
    private static string FindThemesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Solo.Api", "Themes");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/Solo.Api/Themes from the test output directory.");
    }
}
