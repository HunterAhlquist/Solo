using System.Globalization;
using Solo.Shared.Dtos;

namespace Solo.Client.Theming;

/// <summary>
/// A theme background as the custom properties its backdrop element reads:
/// <c>--{prefix}-image</c>, <c>-size</c>, <c>-position</c>, <c>-scrim</c> and
/// <c>-color</c>. Nothing in app.css is written for a particular theme; a
/// property the theme leaves out is simply not set, and the stylesheet's
/// fallback stands.
/// </summary>
internal static class ThemeBackgroundCss
{
    /// <param name="background">Already resolved to the scheme in force (see <see cref="ThemeState"/>).</param>
    /// <param name="prefix">Which backdrop: <c>signin</c> or <c>backdrop</c>.</param>
    /// <param name="absolute">The app-relative URL the API returns, made absolute.</param>
    public static string Properties(ThemeBackgroundDto? background, string prefix, Func<string, Uri> absolute)
    {
        if (background is not { Image: { Length: > 0 } image })
        {
            return string.Empty;
        }

        // Absolute, not the app-relative path the API returns. A relative
        // url() inside a custom property resolves against the stylesheet
        // that *consumes* the variable — app.css, under /css/ — rather than
        // the document base, so the relative form 404s.
        //
        // The URL comes from the instance's own theme catalog, which only
        // ever yields api/themes/... paths, so there is nothing here for a
        // quote to break out of.
        var style = $"--{prefix}-image:url('{absolute(image)}');--{prefix}-size:{Size(background.Fit)};";

        if (background.Position is { Length: > 0 } position)
        {
            style += $"--{prefix}-position:{position};";
        }

        if (background.Scrim > 0)
        {
            style += $"--{prefix}-scrim:{background.Scrim.ToString(CultureInfo.InvariantCulture)};";
        }

        if (background.Color is { Length: > 0 } color)
        {
            style += $"--{prefix}-color:{color};";
        }

        return style;
    }

    /// <summary>
    /// Fit mode to <c>background-size</c>. Width and Height pin one axis and let
    /// the other follow the aspect ratio; Cover and Contain let the browser
    /// choose which axis that is.
    /// </summary>
    private static string Size(ThemeBackgroundFit fit) => fit switch
    {
        ThemeBackgroundFit.Contain => "contain",
        ThemeBackgroundFit.Width => "100% auto",
        ThemeBackgroundFit.Height => "auto 100%",
        ThemeBackgroundFit.Stretch => "100% 100%",
        _ => "cover",
    };
}
