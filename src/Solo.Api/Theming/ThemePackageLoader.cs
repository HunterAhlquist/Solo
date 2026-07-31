using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Solo.Shared.Dtos;

namespace Solo.Api.Theming;

/// <summary>
/// Reads a theme package — a zip with <c>theme.json</c> at its root and content
/// files beneath it — into a validated <see cref="ThemePackage"/>.
/// </summary>
/// <remarks>
/// <para>Packages arrive by being dropped in the themes volume, so the author is
/// whoever can write to that volume: the same person who deploys the app. The
/// checks here are not a sandbox for hostile input — a custom stylesheet on the
/// app's own origin can restyle anything regardless — they are what keeps a
/// malformed, truncated or absurdly large zip from taking the instance down, and
/// what turns "the theme didn't work" into a message naming the reason.</para>
///
/// <para>The one thing they do defend properly is the file system: a zip entry
/// is never written to disk and never escapes the package (zip slip), because
/// entries are read into memory by normalized relative path and nothing else.</para>
/// </remarks>
public static partial class ThemePackageLoader
{
    /// <summary>Compressed size of the zip itself. Background photos are the reason this isn't smaller.</summary>
    public const long MaxArchiveBytes = 12L * 1024 * 1024;

    /// <summary>Total uncompressed bytes across all entries — the zip-bomb ceiling.</summary>
    public const long MaxTotalBytes = 48L * 1024 * 1024;

    public const long MaxEntryBytes = 12L * 1024 * 1024;
    public const int MaxEntries = 200;

    public const string ManifestName = "theme.json";

    /// <summary>Content lives under this folder; nothing else is served.</summary>
    public const string ContentFolder = "content/";

    /// <summary>
    /// Path of the generated <c>@font-face</c> stylesheet. At the package root,
    /// where <see cref="IsAllowedContent"/> refuses every package entry, so it
    /// can never collide with a file an author shipped.
    /// </summary>
    public const string FontStylesheetName = "fonts.css";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
    };

    /// <summary>Extension to the <c>format()</c> hint its <c>@font-face</c> src needs.</summary>
    private static readonly Dictionary<string, string> FontFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".woff2"] = "woff2",
        [".woff"] = "woff",
        [".ttf"] = "truetype",
        [".otf"] = "opentype",
    };

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        // Authors hand-write these; don't punish them for casing or a trailing comma.
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>Loads a <c>.zip</c> theme package.</summary>
    /// <exception cref="ThemePackageException">The package is malformed, oversized or invalid.</exception>
    public static ThemePackage LoadZip(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxArchiveBytes)
        {
            throw new ThemePackageException(
                $"Archive is {info.Length / 1024 / 1024} MB; the limit is {MaxArchiveBytes / 1024 / 1024} MB.");
        }

        using var archive = OpenArchive(path);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var count = 0;

        foreach (var entry in archive.Entries)
        {
            // Directory markers have an empty name and no content.
            if (entry.Name.Length == 0)
            {
                continue;
            }

            if (++count > MaxEntries)
            {
                throw new ThemePackageException($"Archive holds more than {MaxEntries} files.");
            }

            var relative = NormalizeEntryPath(entry.FullName);
            if (relative is null)
            {
                throw new ThemePackageException($"Refusing entry with an unsafe path: '{entry.FullName}'.");
            }

            // Declared length is the header's claim; ReadExactly below is bounded
            // by the buffer, so a lying header truncates rather than expands.
            if (entry.Length > MaxEntryBytes)
            {
                throw new ThemePackageException(
                    $"'{relative}' is {entry.Length / 1024 / 1024} MB; the per-file limit is " +
                    $"{MaxEntryBytes / 1024 / 1024} MB.");
            }

            total += entry.Length;
            if (total > MaxTotalBytes)
            {
                throw new ThemePackageException(
                    $"Archive expands to more than {MaxTotalBytes / 1024 / 1024} MB.");
            }

            files[relative] = ReadEntry(entry, relative);
        }

        return Build(files, source: Path.GetFileName(path), isBuiltIn: false);
    }

    /// <summary>
    /// Loads a theme laid out as a plain directory. Used for the themes shipped
    /// inside the app, so a built-in goes through exactly the same manifest
    /// parsing, validation and URL rewriting as a packaged one — and the built-in
    /// folders double as worked examples for anyone authoring a zip.
    /// </summary>
    public static ThemePackage LoadDirectory(string directory, bool isBuiltIn)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizeEntryPath(Path.GetRelativePath(root, file));
            if (relative is null)
            {
                continue;
            }

            var length = new FileInfo(file).Length;
            total += length;
            if (length > MaxEntryBytes || total > MaxTotalBytes || files.Count >= MaxEntries)
            {
                throw new ThemePackageException($"Theme directory '{root}' exceeds the package size limits.");
            }

            files[relative] = File.ReadAllBytes(file);
        }

        return Build(files, source: isBuiltIn ? "built-in" : Path.GetFileName(root), isBuiltIn);
    }

    private static ZipArchive OpenArchive(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException e)
        {
            throw new ThemePackageException($"Not a readable zip archive: {e.Message}");
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, string relative)
    {
        var buffer = new byte[entry.Length];
        using var stream = entry.Open();
        stream.ReadExactly(buffer);

        // A second byte past the declared length means the header understated the
        // payload — the classic bomb shape, and the one place the ceilings above
        // could be walked past.
        if (stream.ReadByte() != -1)
        {
            throw new ThemePackageException($"'{relative}' is larger than its zip entry declares.");
        }

        return buffer;
    }

    /// <summary>
    /// Reduces a zip entry name to a safe package-relative path, or null if it
    /// escapes the package, is absolute, or is a type themes may not carry.
    /// </summary>
    private static string? NormalizeEntryPath(string raw)
    {
        var path = raw.Replace('\\', '/').TrimStart('/');
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':'))
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            return null;
        }

        path = string.Join('/', segments);

        // Content paths are emitted into URLs and, for fonts, into a generated
        // url() in CSS. Restricting them to characters that need no escaping in
        // either means neither emitter has to think about quoting. It rules out
        // spaces in filenames, which is no loss for a web asset.
        if (!SafePathCharacters().IsMatch(path))
        {
            return null;
        }

        // Editor and OS litter that would otherwise fail the extension check.
        if (segments.Any(s => s.StartsWith('.')) || path.Contains("__MACOSX", StringComparison.Ordinal))
        {
            return null;
        }

        var isManifest = string.Equals(path, ManifestName, StringComparison.OrdinalIgnoreCase);
        return isManifest || IsAllowedContent(path) ? path : null;
    }

    private static bool IsAllowedContent(string path) =>
        path.StartsWith(ContentFolder, StringComparison.OrdinalIgnoreCase)
        && ContentTypes.ContainsKey(Path.GetExtension(path));

    private static ThemePackage Build(Dictionary<string, byte[]> files, string source, bool isBuiltIn)
    {
        if (!files.TryGetValue(ManifestName, out var manifestBytes))
        {
            throw new ThemePackageException($"No {ManifestName} at the root of the package.");
        }

        var manifest = ParseManifest(manifestBytes);
        var content = files
            .Where(f => !string.Equals(f.Key, ManifestName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                f => f.Key,
                f => new ThemeContent(f.Value, ContentTypes[Path.GetExtension(f.Key)]),
                StringComparer.OrdinalIgnoreCase);

        manifest = Validate(manifest, content, isBuiltIn);

        // Generated after validation, because it is built from paths validation
        // has already proved exist, and added to the content map so it is
        // served, hashed and cached exactly like a file the author shipped.
        if (manifest.Fonts is { Count: > 0 } fonts)
        {
            content[FontStylesheetName] = new ThemeContent(
                Encoding.UTF8.GetBytes(BuildFontStylesheet(fonts)), ContentTypes[".css"]);
            manifest = manifest with { FontStylesheet = FontStylesheetName };
        }

        return new ThemePackage
        {
            Manifest = manifest,
            Content = content,
            ContentHash = Hash(manifestBytes, content),
            Source = source,
        };
    }

    /// <summary>
    /// Builds the <c>@font-face</c> block for a package's fonts.
    /// </summary>
    /// <remarks>
    /// The <c>url()</c> values are package-relative, and this stylesheet is
    /// served from the package root — so they resolve against it to the same
    /// hashed, immutable URLs the font files are served from, with no absolute
    /// URL baked in. That matters for the desktop shell, which serves the same
    /// bytes from its own origin.
    /// </remarks>
    private static string BuildFontStylesheet(IReadOnlyList<ThemeFontDto> fonts)
    {
        var css = new StringBuilder(
            "/* Generated from theme.json `fonts`. Edit the manifest, not this file. */\n");

        foreach (var font in fonts)
        {
            var format = FontFormats[Path.GetExtension(font.Src)];
            css.Append("\n@font-face {\n")
                .Append($"    font-family: '{font.Family}';\n")
                .Append($"    src: url('{font.Src}') format('{format}');\n")
                .Append($"    font-weight: {font.Weight ?? "normal"};\n")
                .Append($"    font-style: {font.Style ?? "normal"};\n")
                .Append($"    font-display: {font.Display ?? "swap"};\n")
                .Append("}\n");
        }

        return css.ToString();
    }

    private static ThemeDto ParseManifest(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<ThemeDto>(bytes, ManifestJson)
                ?? throw new ThemePackageException($"{ManifestName} is empty.");
        }
        catch (JsonException e)
        {
            throw new ThemePackageException($"{ManifestName} is not valid JSON: {e.Message}");
        }
    }

    /// <summary>
    /// Checks the manifest describes something renderable, then fills in what it
    /// left implicit (contrast text, clamped scrim).
    /// </summary>
    private static ThemeDto Validate(
        ThemeDto manifest, IReadOnlyDictionary<string, ThemeContent> content, bool isBuiltIn)
    {
        if (manifest.FormatVersion != 1)
        {
            throw new ThemePackageException(
                $"Unsupported formatVersion {manifest.FormatVersion}; this instance reads version 1.");
        }

        if (!ThemeId().IsMatch(manifest.Id))
        {
            throw new ThemePackageException(
                $"'{manifest.Id}' is not a usable theme id: use lower-case letters, digits and hyphens (2-64 chars).");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new ThemePackageException("The theme has no name.");
        }

        string? Resolve(string? path, string field)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            // Content is keyed case-insensitively, so the manifest's spelling of
            // a path resolves to the file whatever case the zip stored it under.
            var normalized = NormalizeEntryPath(path);
            if (normalized is null || !content.ContainsKey(normalized))
            {
                throw new ThemePackageException(
                    $"{field} points at '{path}', which is not a content file in this package.");
            }

            return normalized;
        }

        var login = manifest.Login;
        if (login is not null)
        {
            var background = login.Background;
            if (background is not null)
            {
                if (background.Color is { } color && !ThemeColors.IsValid(color))
                {
                    throw new ThemePackageException($"login.background.color '{color}' is not a valid CSS colour.");
                }

                if (background.Position is { } position && !BackgroundPosition().IsMatch(position))
                {
                    throw new ThemePackageException(
                        $"login.background.position '{position}' is not a CSS background-position.");
                }

                background = background with
                {
                    Image = Resolve(background.Image, "login.background.image"),
                    ImageLight = Resolve(background.ImageLight, "login.background.imageLight"),
                    Scrim = Math.Clamp(background.Scrim, 0, 1),
                };
            }

            login = login with
            {
                Background = background,
                Logo = Resolve(login.Logo, "login.logo"),
                LogoLight = Resolve(login.LogoLight, "login.logoLight"),
            };
        }

        return manifest with
        {
            IsBuiltIn = isBuiltIn,
            Stylesheet = Resolve(manifest.Stylesheet, "stylesheet"),
            Login = login,
            Fonts = ValidateFonts(manifest.Fonts, Resolve),
            // Output only: a manifest that sets these is ignored. FontStylesheet
            // is generated below; the fallback flags are decided per request by
            // whoever resolved the package.
            FontStylesheet = null,
            IsFallback = false,
            RequestedThemeId = null,
            Typography = NormalizeTypography(manifest.Typography),
            Dark = NormalizePalette(manifest.Dark, "dark"),
            Light = NormalizePalette(manifest.Light, "light"),
        };
    }

    /// <summary>
    /// Checks each declared font names a real file of a real font type, and that
    /// every value going into the generated CSS is one of a known set.
    /// </summary>
    private static IReadOnlyList<ThemeFontDto>? ValidateFonts(
        IReadOnlyList<ThemeFontDto>? fonts, Func<string?, string, string?> resolve)
    {
        if (fonts is null || fonts.Count == 0)
        {
            return null;
        }

        var validated = new List<ThemeFontDto>(fonts.Count);
        for (var i = 0; i < fonts.Count; i++)
        {
            var font = fonts[i];
            var where = $"fonts[{i}]";

            var family = font.Family?.Trim().Trim('\'', '"').Trim() ?? string.Empty;
            if (!FontFamilyName().IsMatch(family))
            {
                throw new ThemePackageException($"{where}.family '{font.Family}' is not a usable font family name.");
            }

            if (string.IsNullOrWhiteSpace(font.Src))
            {
                throw new ThemePackageException($"{where}.src is missing; it must point at a font file in the package.");
            }

            var src = resolve(font.Src, $"{where}.src")!;
            if (!FontFormats.ContainsKey(Path.GetExtension(src)))
            {
                throw new ThemePackageException(
                    $"{where}.src '{font.Src}' is not a font file; use .woff2, .woff, .ttf or .otf.");
            }

            validated.Add(font with
            {
                Family = family,
                Src = src,
                Weight = Check(font.Weight, FontWeight(), $"{where}.weight"),
                Style = Check(font.Style, FontStyle(), $"{where}.style"),
                Display = Check(font.Display, FontDisplay(), $"{where}.display"),
            });
        }

        return validated;

        static string? Check(string? value, Regex allowed, string where)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return allowed.IsMatch(trimmed)
                ? trimmed
                : throw new ThemePackageException($"{where} '{value}' is not a value CSS accepts here.");
        }
    }

    /// <summary>
    /// Strips quotes from font family names.
    ///
    /// <para>MudBlazor quotes any family whose name contains a space when it
    /// writes the CSS variable, so a manifest that quotes them too produces
    /// <c>''Segoe UI''</c> — invalid, which makes the whole <c>font-family</c>
    /// declaration invalid at computed-value time and silently drops the theme's
    /// typography. Quoting is the natural thing for an author to write, so
    /// normalize rather than reject.</para>
    /// </summary>
    private static ThemeTypographyDto? NormalizeTypography(ThemeTypographyDto? typography)
    {
        if (typography is null)
        {
            return null;
        }

        return typography with
        {
            FontFamily = Clean(typography.FontFamily, nameof(ThemeTypographyDto.FontFamily)),
            HeadingFontFamily = Clean(typography.HeadingFontFamily, nameof(ThemeTypographyDto.HeadingFontFamily)),
        };

        static IReadOnlyList<string>? Clean(IReadOnlyList<string>? families, string field)
        {
            if (families is null)
            {
                return null;
            }

            var cleaned = new List<string>(families.Count);
            foreach (var family in families)
            {
                var name = family.Trim().Trim('\'', '"').Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                // The name lands in a CSS declaration value; nothing that could
                // close it or open a comment or a url() belongs in a font name.
                if (!FontFamilyName().IsMatch(name))
                {
                    throw new ThemePackageException(
                        $"typography.{char.ToLowerInvariant(field[0])}{field[1..]} entry '{family}' " +
                        "is not a usable font family name.");
                }

                cleaned.Add(name);
            }

            return cleaned.Count > 0 ? cleaned : null;
        }
    }

    /// <summary>
    /// Rejects unusable colours outright rather than dropping them: a palette
    /// entry that silently vanishes shows up as one wrong-coloured control
    /// somewhere, which is far harder to diagnose than a refused package.
    /// </summary>
    private static ThemePaletteDto? NormalizePalette(ThemePaletteDto? palette, string scheme)
    {
        if (palette is null)
        {
            return null;
        }

        foreach (var property in typeof(ThemePaletteDto).GetProperties())
        {
            if (property.GetValue(palette) is string value && !ThemeColors.IsValid(value))
            {
                throw new ThemePackageException(
                    $"{scheme}.{char.ToLowerInvariant(property.Name[0])}{property.Name[1..]} " +
                    $"'{value}' is not a valid CSS colour.");
            }
        }

        // Filled controls take their label colour from a static *ContrastText.
        // Derive the readable one wherever the author didn't choose.
        return palette with
        {
            PrimaryContrastText = palette.PrimaryContrastText ?? Contrast(palette.Primary),
            SecondaryContrastText = palette.SecondaryContrastText ?? Contrast(palette.Secondary),
            TertiaryContrastText = palette.TertiaryContrastText ?? Contrast(palette.Tertiary),
            SuccessContrastText = palette.SuccessContrastText ?? Contrast(palette.Success),
            WarningContrastText = palette.WarningContrastText ?? Contrast(palette.Warning),
            ErrorContrastText = palette.ErrorContrastText ?? Contrast(palette.Error),
            InfoContrastText = palette.InfoContrastText ?? Contrast(palette.Info),
        };

        static string? Contrast(string? fill) => fill is null ? null : ThemeColors.ContrastText(fill);
    }

    /// <summary>
    /// Digest over the manifest and every content file, in a stable order. Rides
    /// in content URLs, so changing any byte of a package changes every URL it
    /// serves and no stale copy can be cached under the new package.
    /// </summary>
    private static string Hash(byte[] manifestBytes, IReadOnlyDictionary<string, ThemeContent> content)
    {
        using var sha = SHA256.Create();
        sha.TransformBlock(manifestBytes, 0, manifestBytes.Length, null, 0);

        foreach (var (path, file) in content.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(path);
            sha.TransformBlock(name, 0, name.Length, null, 0);
            sha.TransformBlock(file.Bytes, 0, file.Bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!)[..16].ToLower(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}[a-z0-9]$")]
    private static partial Regex ThemeId();

    // Keywords, lengths and percentages only — the same no-url()/no-brace rule
    // the colour validator applies, for the same reason.
    [GeneratedRegex(@"^[a-zA-Z0-9%.\s-]{1,40}$")]
    private static partial Regex BackgroundPosition();

    // Letters, digits, spaces and hyphens covers every real family name and the
    // CSS generics; a leading hyphen covers -apple-system.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9 -]{0,62}$|^-[a-zA-Z][a-zA-Z0-9 -]{0,62}$")]
    private static partial Regex FontFamilyName();

    // Safe in a URL path and inside a CSS url('...') without escaping.
    [GeneratedRegex(@"^[A-Za-z0-9._/-]+$")]
    private static partial Regex SafePathCharacters();

    // A keyword, one weight, or a variable-font range ("100 900").
    [GeneratedRegex(@"^(normal|bold|[1-9]00|1000)( ([1-9]00|1000))?$")]
    private static partial Regex FontWeight();

    [GeneratedRegex("^(normal|italic|oblique)$")]
    private static partial Regex FontStyle();

    [GeneratedRegex("^(auto|block|swap|fallback|optional)$")]
    private static partial Regex FontDisplay();
}
