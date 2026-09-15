namespace Solo.Shared.Dtos;

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
