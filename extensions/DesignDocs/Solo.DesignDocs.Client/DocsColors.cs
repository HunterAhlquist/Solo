using System.Globalization;

namespace Solo.DesignDocs.Client;

/// <summary>
/// A member's color from their slot, as js/src/colors.js computes it: golden-angle
/// hue steps in OKLCH. The presence bar draws it with CSS oklch(), which every
/// browser the app supports reads; the editor converts to rgb() itself.
/// </summary>
public static class DocsColors
{
    private const double GoldenAngle = 137.508;

    public static double HueOf(int slot) => ((slot * GoldenAngle) % 360 + 360) % 360;

    public static string Css(int slot) =>
        string.Create(CultureInfo.InvariantCulture, $"oklch(0.58 0.14 {HueOf(slot):0.###})");
}
