namespace Solo.Shared.Dtos;

/// <summary>
/// How a login background image fills the viewport.
/// <see cref="Width"/> and <see cref="Height"/> scale the image so that one axis
/// matches the viewport exactly and the other is free to overflow or fall short;
/// <see cref="Cover"/> and <see cref="Contain"/> pick that axis automatically.
/// </summary>
public enum ThemeBackgroundFit
{
    /// <summary>Fill the viewport, cropping the overflowing axis. The usual choice for a photo.</summary>
    Cover = 0,

    /// <summary>Fit the whole image in view, letterboxing the short axis.</summary>
    Contain = 1,

    /// <summary>Match the viewport width; height follows the aspect ratio.</summary>
    Width = 2,

    /// <summary>Match the viewport height; width follows the aspect ratio.</summary>
    Height = 3,

    /// <summary>Match both axes, distorting the aspect ratio.</summary>
    Stretch = 4,
}

/// <summary>
/// One MudBlazor palette. Every value is a CSS colour string, validated on
/// import (see <c>ThemeColors</c>) because MudBlazor writes these straight into
/// a <c>&lt;style&gt;</c> block as custom-property values.
/// </summary>
/// <remarks>
/// A subset of <c>MudBlazor.Palette</c> — the properties a theme actually needs
/// to control. Anything omitted keeps MudBlazor's own default for that scheme,
/// so a minimal theme can set four colours and still render coherently.
/// </remarks>
public sealed record ThemePaletteDto
{
    public string? Primary { get; init; }
    public string? Secondary { get; init; }
    public string? Tertiary { get; init; }

    /// <summary>
    /// Text drawn on top of a filled <c>Primary</c> surface. MudBlazor has no
    /// contrast detection and defaults these to white, so a light accent gets
    /// unreadable labels. Left null they are derived from the fill's luminance
    /// at import time, which is why most themes should omit them.
    /// </summary>
    public string? PrimaryContrastText { get; init; }
    public string? SecondaryContrastText { get; init; }
    public string? TertiaryContrastText { get; init; }
    public string? SuccessContrastText { get; init; }
    public string? WarningContrastText { get; init; }
    public string? ErrorContrastText { get; init; }
    public string? InfoContrastText { get; init; }

    public string? Background { get; init; }
    public string? BackgroundGray { get; init; }
    public string? Surface { get; init; }

    public string? AppbarBackground { get; init; }
    public string? AppbarText { get; init; }
    public string? DrawerBackground { get; init; }
    public string? DrawerText { get; init; }
    public string? DrawerIcon { get; init; }

    public string? TextPrimary { get; init; }
    public string? TextSecondary { get; init; }
    public string? TextDisabled { get; init; }

    public string? ActionDefault { get; init; }
    public string? ActionDisabled { get; init; }
    public string? ActionDisabledBackground { get; init; }

    public string? Divider { get; init; }
    public string? DividerLight { get; init; }
    public string? LinesDefault { get; init; }
    public string? LinesInputs { get; init; }
    public string? TableLines { get; init; }
    public string? TableStriped { get; init; }
    public string? TableHover { get; init; }

    public string? Success { get; init; }
    public string? Warning { get; init; }
    public string? Error { get; init; }
    public string? Info { get; init; }

    public string? OverlayDark { get; init; }
    public string? OverlayLight { get; init; }
}

/// <summary>
/// A font file shipped inside the package, declared so the API can generate the
/// <c>@font-face</c> rule for it.
/// </summary>
/// <remarks>
/// A theme could hand-write the same rule in its stylesheet, but then the path
/// is checked by nobody: a typo is a silent fallback to a system font at render
/// time. Declared here, the file is verified to exist when the package is
/// imported, like every other asset a theme points at.
/// </remarks>
public sealed record ThemeFontDto
{
    /// <summary>The name <c>typography.fontFamily</c> then refers to.</summary>
    public string Family { get; init; } = string.Empty;

    /// <summary>Package-relative path to the font file; a URL once served.</summary>
    public string Src { get; init; } = string.Empty;

    /// <summary>A single weight (<c>400</c>), a keyword, or a variable-font range (<c>100 900</c>).</summary>
    public string? Weight { get; init; }

    /// <summary><c>normal</c>, <c>italic</c> or <c>oblique</c>.</summary>
    public string? Style { get; init; }

    /// <summary>
    /// CSS <c>font-display</c>. Defaults to <c>swap</c>: the app is readable in a
    /// fallback face immediately rather than blank while the font downloads.
    /// </summary>
    public string? Display { get; init; }
}

/// <summary>
/// Font stacks and sizes. <see cref="FontFamily"/> is a CSS font-family list;
/// write the names unquoted, because MudBlazor quotes them when it emits the
/// CSS variable and quoting here would double them into invalid CSS.
/// </summary>
public sealed record ThemeTypographyDto
{
    public IReadOnlyList<string>? FontFamily { get; init; }

    /// <summary>Optional distinct stack for h1-h6; falls back to <see cref="FontFamily"/>.</summary>
    public IReadOnlyList<string>? HeadingFontFamily { get; init; }

    public string? FontSize { get; init; }
    public string? HeadingWeight { get; init; }
    public string? ButtonWeight { get; init; }
    public string? LetterSpacing { get; init; }
}

public sealed record ThemeLayoutDto
{
    public string? BorderRadius { get; init; }
    public string? AppbarHeight { get; init; }
    public string? DrawerWidth { get; init; }
}

/// <summary>
/// A login background. <see cref="Image"/> is a package-relative path in
/// <c>theme.json</c> and an app-relative URL by the time a client sees it.
/// </summary>
public sealed record ThemeBackgroundDto
{
    /// <summary>Used for both schemes unless <see cref="ImageLight"/> overrides it.</summary>
    public string? Image { get; init; }

    /// <summary>
    /// Optional light-scheme variant. A photo chosen to sit behind a dark card
    /// usually fights a light one, so themes can ship both.
    /// </summary>
    public string? ImageLight { get; init; }

    public ThemeBackgroundFit Fit { get; init; } = ThemeBackgroundFit.Cover;

    /// <summary>CSS <c>background-position</c>, e.g. <c>"center"</c> or <c>"50% 20%"</c>.</summary>
    public string? Position { get; init; }

    /// <summary>
    /// 0-1 opacity of a flat scrim laid over the image in the palette's
    /// background colour. Keeps the sign-in card legible over a busy photo.
    /// </summary>
    public double Scrim { get; init; }

    /// <summary>Solid colour behind the image, seen where <c>Contain</c> letterboxes.</summary>
    public string? Color { get; init; }
}

public sealed record ThemeLoginDto
{
    public ThemeBackgroundDto? Background { get; init; }

    /// <summary>Optional wordmark shown on the sign-in card in place of the "Solo" text.</summary>
    public string? Logo { get; init; }

    /// <summary>Optional light-scheme variant of <see cref="Logo"/>.</summary>
    public string? LogoLight { get; init; }
}

/// <summary>
/// A theme package: the deserialized <c>theme.json</c>, and — once served — the
/// same document with every content path rewritten to a URL under
/// <c>/api/themes/{id}/{hash}/content/</c>.
/// </summary>
/// <remarks>
/// One record for both roles on purpose. The manifest an author writes and the
/// payload a client renders describe the same thing; keeping them as one type
/// means a new theme property is added in exactly one place.
/// </remarks>
public sealed record ThemeDto
{
    /// <summary>Schema version, so a future breaking change can be detected rather than guessed at.</summary>
    public int FormatVersion { get; init; } = 1;

    /// <summary>Stable slug (lower-case, <c>a-z0-9-</c>), unique per instance. Stored as the active theme.</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Author { get; init; }

    /// <summary>True for the themes shipped inside the app rather than dropped in the themes volume.</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// Set when this is not the theme the instance asked for: the configured one
    /// is no longer installed and the default was served in its place. Output
    /// only — a manifest never sets it.
    /// </summary>
    public bool IsFallback { get; init; }

    /// <summary>The missing theme id, when <see cref="IsFallback"/> is set. Output only.</summary>
    public string? RequestedThemeId { get; init; }

    /// <summary>Optional custom stylesheet, loaded after the app's own CSS.</summary>
    public string? Stylesheet { get; init; }

    /// <summary>Fonts shipped in the package, declared in <c>theme.json</c>.</summary>
    public IReadOnlyList<ThemeFontDto>? Fonts { get; init; }

    /// <summary>
    /// URL of the <c>@font-face</c> stylesheet the API generates from
    /// <see cref="Fonts"/>. Null when the theme ships no fonts. Output only —
    /// a manifest never sets this.
    /// </summary>
    public string? FontStylesheet { get; init; }

    public ThemeTypographyDto? Typography { get; init; }
    public ThemeLayoutDto? Layout { get; init; }
    public ThemeLoginDto? Login { get; init; }

    public ThemePaletteDto? Dark { get; init; }
    public ThemePaletteDto? Light { get; init; }
}

/// <summary>A theme as listed in the admin picker, without the palettes.</summary>
public sealed record ThemeSummaryDto(
    string Id,
    string Name,
    string? Description,
    string? Author,
    bool IsBuiltIn,
    bool IsActive);

/// <summary>
/// Result of rescanning the themes volume, so an admin who just dropped in a
/// zip can see whether it was accepted and why not.
/// </summary>
public sealed record ThemeScanResultDto(
    IReadOnlyList<ThemeSummaryDto> Themes,
    IReadOnlyList<ThemeScanErrorDto> Errors);

public sealed record ThemeScanErrorDto(string File, string Message);
