using System.Globalization;
using System.Text.RegularExpressions;
using Solo.Shared.Dtos;

namespace Solo.Api.Email;

/// <summary>What an email is branded with: the instance's name and its theme.</summary>
public sealed record EmailBrand(string Name, ThemeDto Theme);

/// <summary>The colors one scheme of an email uses, taken from a theme palette.</summary>
/// <remarks>
/// Mail apps render plain hex most reliably, so every value is reduced to six
/// digits: a translucent theme color is blended over the surface it sits on,
/// and anything else a theme may hold (a named color, <c>rgba()</c>) falls back.
/// </remarks>
public sealed record EmailPalette(
    string Page, string Surface, string Border, string Header, string HeaderText,
    string Text, string Muted, string Rule, string Accent, string AccentText, string Danger)
{
    private static readonly EmailPalette DefaultLight = new(
        "#f8f1e8", "#fffdfa", "#dccfbb", "#17160f", "#ffe000",
        "#231d16", "#5b4f42", "#e4d9c9", "#df6610", "#ffffff", "#b3261e");

    private static readonly EmailPalette DefaultDark = new(
        "#080808", "#101010", "#3f3d10", "#000000", "#fff600",
        "#ffffff", "#b0b0a0", "#403e10", "#fff600", "#000000", "#ff5252");

    public static EmailPalette Light(ThemeDto theme) => From(theme.Light ?? theme.Dark, DefaultLight);

    public static EmailPalette Dark(ThemeDto theme) => From(theme.Dark ?? theme.Light, DefaultDark);

    private static EmailPalette From(ThemePaletteDto? p, EmailPalette fallback)
    {
        if (p is null)
        {
            return fallback;
        }

        var surface = Hex(p.Surface, null) ?? fallback.Surface;
        var page = Hex(p.BackgroundGray ?? p.Background, surface) ?? fallback.Page;
        var accent = Hex(p.Primary, surface) ?? fallback.Accent;
        return new EmailPalette(
            page,
            surface,
            Hex(p.LinesDefault ?? p.Divider, surface) ?? fallback.Border,
            Hex(p.AppbarBackground, null) ?? fallback.Header,
            Hex(p.AppbarText, null) ?? fallback.HeaderText,
            Hex(p.TextPrimary, surface) ?? fallback.Text,
            Hex(p.TextSecondary, surface) ?? fallback.Muted,
            Hex(p.Divider, surface) ?? fallback.Rule,
            accent,
            Hex(p.PrimaryContrastText, accent) ?? (Luminance(accent) > 0.5 ? "#000000" : "#ffffff"),
            Hex(p.Error, surface) ?? fallback.Danger);
    }

    /// <summary>#rgb, #rrggbb or #rrggbbaa as #rrggbb; alpha is blended over <paramref name="over"/>, or dropped without one.</summary>
    private static string? Hex(string? color, string? over)
    {
        if (color is null || !Regex.IsMatch(color, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"))
        {
            return null;
        }

        var digits = color.Length == 4 ? string.Concat(color[1..].Select(c => $"{c}{c}")) : color[1..];
        var rgb = Channels(digits[..6]);
        if (digits.Length == 8 && over is not null)
        {
            var alpha = int.Parse(digits[6..], NumberStyles.HexNumber) / 255.0;
            var under = Channels(over[1..]);
            rgb = rgb.Zip(under, (top, bottom) => (int)Math.Round(top * alpha + bottom * (1 - alpha))).ToArray();
        }

        return $"#{rgb[0]:x2}{rgb[1]:x2}{rgb[2]:x2}";
    }

    private static int[] Channels(string hex) =>
        [int.Parse(hex[..2], NumberStyles.HexNumber), int.Parse(hex[2..4], NumberStyles.HexNumber), int.Parse(hex[4..6], NumberStyles.HexNumber)];

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    private static double Luminance(string hex)
    {
        var c = Channels(hex[1..]).Select(v => v / 255.0).Select(v => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4)).ToArray();
        return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
    }
}

/// <summary>The theme's fonts and corner radius, made safe to put in a style attribute.</summary>
public sealed record EmailType(string Body, string Heading, int Radius)
{
    // Theme fonts ship as files the mail app can't load, so the stacks end in
    // something every device has.
    private const string SansFallback = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";
    private const string MonoFallback = "'Courier New',Courier,monospace";

    public static EmailType From(ThemeDto theme)
    {
        var body = Stack(theme.Typography?.FontFamily);
        var heading = Stack(theme.Typography?.HeadingFontFamily) ?? body;
        return new EmailType(body ?? SansFallback, heading ?? SansFallback, RadiusOf(theme.Layout?.BorderRadius));
    }

    private static string? Stack(IReadOnlyList<string>? families)
    {
        var names = families?
            .Select(f => f.Trim())
            .Where(f => Regex.IsMatch(f, "^[A-Za-z0-9 _.-]{1,60}$"))
            .ToList();
        if (names is not { Count: > 0 })
        {
            return null;
        }

        var generic = names.Contains("monospace") ? MonoFallback : SansFallback;
        return string.Join(",", names.Where(n => n is not ("monospace" or "sans-serif" or "serif")).Select(n => n.Contains(' ') ? $"'{n}'" : n)) +
               "," + generic;
    }

    private static int RadiusOf(string? value)
    {
        var match = value is null ? null : Regex.Match(value, @"^(\d+(?:\.\d+)?)(px|rem|em)$");
        if (match is not { Success: true })
        {
            return 8;
        }

        var size = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return (int)Math.Round(Math.Min(match.Groups[2].Value == "px" ? size : size * 16, 16));
    }
}
