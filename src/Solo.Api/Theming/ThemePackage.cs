using Solo.Shared.Dtos;

namespace Solo.Api.Theming;

/// <summary>One file inside a theme package.</summary>
public sealed record ThemeContent(byte[] Bytes, string ContentType);

/// <summary>
/// A validated theme: its manifest, its content files, and a hash of both.
/// </summary>
/// <remarks>
/// The hash rides in every content URL, so a re-imported package with changed
/// bytes gets fresh URLs and the old ones can be cached forever.
/// </remarks>
public sealed class ThemePackage
{
    public required ThemeDto Manifest { get; init; }

    /// <summary>Package-relative path (e.g. <c>content/logo.svg</c>) to bytes. Excludes <c>theme.json</c>.</summary>
    public required IReadOnlyDictionary<string, ThemeContent> Content { get; init; }

    /// <summary>Short hex digest over the manifest and every content file.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Where it came from, for admin diagnostics — a zip file name, or "built-in".</summary>
    public required string Source { get; init; }

    public string Id => Manifest.Id;

    /// <summary>
    /// The manifest as a client sees it: every package-relative content path
    /// rewritten to a URL this instance serves. Relative, because the client
    /// resolves against <c>&lt;base href="/"&gt;</c> and the desktop shell proxies
    /// <c>/api/</c> to whichever instance it is pointed at.
    /// </summary>
    public ThemeDto ToServedDto()
    {
        var prefix = $"api/themes/{Manifest.Id}/{ContentHash}/";

        string? Url(string? path) => path is null ? null : prefix + path;

        var login = Manifest.Login;
        if (login is not null)
        {
            login = login with
            {
                Logo = Url(login.Logo),
                LogoLight = Url(login.LogoLight),
                Background = login.Background is { } background
                    ? background with
                    {
                        Image = Url(background.Image),
                        ImageLight = Url(background.ImageLight),
                    }
                    : null,
            };
        }

        return Manifest with
        {
            Stylesheet = Url(Manifest.Stylesheet),
            FontStylesheet = Url(Manifest.FontStylesheet),
            Login = login,
            // The font files' own URLs are resolved by the browser against the
            // generated stylesheet, so the paths inside it stay package-relative.
            Fonts = Manifest.Fonts,
        };
    }

    public ThemeSummaryDto ToSummary(bool isActive) => new(
        Manifest.Id,
        Manifest.Name,
        Manifest.Description,
        Manifest.Author,
        Manifest.IsBuiltIn,
        isActive);
}

/// <summary>A package that failed to load, kept so the admin UI can explain why.</summary>
public sealed record ThemeLoadFailure(string File, string Message);

/// <summary>Raised by the loader for anything that makes a package unusable.</summary>
public sealed class ThemePackageException(string message) : Exception(message);
