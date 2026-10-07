using System.Globalization;
using Solo.Roadmap.Contracts;

namespace Solo.Roadmap.Client;

/// <summary>The milestone dialog's swatches, and the ink that reads on a flag of any colour.</summary>
public static class MilestoneColors
{
    /// <summary>Saturated enough to stand out from the bars and stripes in either colour scheme.</summary>
    public static readonly IReadOnlyList<string> Swatches =
    [
        RoadmapLimits.DefaultMilestoneColor,
        "#fb8c00",
        "#fdd835",
        "#43a047",
        "#00acc1",
        "#1e88e5",
        "#8e24aa",
        "#d81b60",
    ];

    /// <summary>
    /// Black or white for text on <paramref name="hex"/>, by WCAG relative
    /// luminance: a yellow flag gets black text, a red one white.
    /// </summary>
    /// <remarks>
    /// The two contrasts are equal at a luminance of about 0.18, which would hand
    /// the default red black text. The cut-off sits higher, leaning to white:
    /// on a saturated mid-tone at flag size, white reads better than the ratio says.
    /// </remarks>
    public static string Ink(string hex)
    {
        if (hex is not ['#', _, _, _, _, _, _] ||
            !int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return "#fff";
        }

        static double Channel(int value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        var luminance = 0.2126 * Channel(rgb >> 16 & 0xff) + 0.7152 * Channel(rgb >> 8 & 0xff) + 0.0722 * Channel(rgb & 0xff);
        return luminance > 0.3 ? "#000" : "#fff";
    }
}
