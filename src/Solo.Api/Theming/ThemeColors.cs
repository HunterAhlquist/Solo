using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Solo.Api.Theming;

/// <summary>
/// Colour handling for theme packages: validating what an author wrote, and
/// working out readable text colours for the ones they left out.
/// </summary>
public static partial class ThemeColors
{
    public const string Black = "#000000";
    public const string White = "#ffffff";

    /// <summary>
    /// Whether a manifest colour is safe to emit.
    ///
    /// <para>MudBlazor renders palette values into a <c>&lt;style&gt;</c> block as
    /// custom-property values — <c>--mud-palette-primary: {value};</c> — so an
    /// unconstrained string is CSS injection, and the closing brace of that block
    /// is one <c>}</c> away. Theme packages come from an operator with filesystem
    /// access, not from the web, so this is a guard against a mistyped or
    /// third-party manifest rather than against an attacker who already has the
    /// volume. It still refuses anything that could terminate the declaration,
    /// open a comment, or reach the network via <c>url()</c>.</para>
    /// </summary>
    public static bool IsValid([NotNullWhen(true)] string? value) =>
        value is not null && (HexColor().IsMatch(value)
            || FunctionalColor().IsMatch(value)
            || NamedColor().IsMatch(value));

    /// <summary>
    /// Black or white, whichever reads better on <paramref name="background"/>.
    ///
    /// <para>MudBlazor has no contrast detection: every <c>*ContrastText</c>
    /// defaults to white, so a light accent colour gets white-on-light labels on
    /// filled buttons. Deriving it here means a theme author only sets these when
    /// they want something other than the readable answer.</para>
    ///
    /// <para>Decided by WCAG contrast ratio rather than the YIQ &gt; 150 shortcut
    /// <c>LabelChip</c> uses for user-picked label colours: the two agree on
    /// almost every colour, and where they differ this one is the answer the
    /// contrast tests check against.</para>
    /// </summary>
    public static string ContrastText(string background) =>
        TryParseHex(background, out var rgb) && ContrastRatio(rgb, (0, 0, 0)) >= ContrastRatio(rgb, (255, 255, 255))
            ? Black
            : White;

    /// <summary>
    /// WCAG 2.1 contrast ratio between two colours, 1:1 (identical) to 21:1
    /// (black on white). AA wants 4.5 for body text and 3.0 for large text and
    /// UI boundaries.
    /// </summary>
    public static double ContrastRatio((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var (lighter, darker) = (RelativeLuminance(a), RelativeLuminance(b));
        if (lighter < darker)
        {
            (lighter, darker) = (darker, lighter);
        }

        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Convenience overload for two hex strings; returns 0 if either won't parse.</summary>
    public static double ContrastRatio(string a, string b) =>
        TryParseHex(a, out var left) && TryParseHex(b, out var right) ? ContrastRatio(left, right) : 0;

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double RelativeLuminance((int R, int G, int B) rgb)
    {
        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(rgb.R)) + (0.7152 * Channel(rgb.G)) + (0.0722 * Channel(rgb.B));
    }

    /// <summary>
    /// Parses <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c> and <c>#rrggbbaa</c>.
    /// Alpha is dropped: these colours are composited over a background this
    /// code doesn't know, so treating them as opaque is the honest approximation.
    /// </summary>
    public static bool TryParseHex(string? value, out (int R, int G, int B) rgb)
    {
        rgb = default;
        if (value is null || !HexColor().IsMatch(value))
        {
            return false;
        }

        var hex = value[1..];
        if (hex.Length is 3 or 4)
        {
            // #abc -> #aabbcc
            hex = string.Concat(hex[..3].Select(c => new string(c, 2)));
        }

        rgb = (
            int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return true;
    }

    [GeneratedRegex(@"^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColor();

    // Deliberately no '*' (would allow a /* comment) and no letters beyond the
    // function name, so url(), var() and attr() cannot appear.
    [GeneratedRegex(@"^(?:rgb|rgba|hsl|hsla)\(\s*[0-9.,%\s/+-]+\)$")]
    private static partial Regex FunctionalColor();

    // CSS named colours plus 'transparent' and 'currentcolor'. Not enumerated —
    // a bare word can't carry a payload.
    [GeneratedRegex("^[a-zA-Z]{3,20}$")]
    private static partial Regex NamedColor();
}
