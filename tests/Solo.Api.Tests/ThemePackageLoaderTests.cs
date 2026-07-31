using System.IO.Compression;
using System.Text;
using Solo.Api.Theming;
using Solo.Shared.Dtos;

namespace Solo.Api.Tests;

/// <summary>
/// The package reader's job is that a bad zip in the themes volume produces a
/// named failure rather than a broken instance. These cover the ways a zip can
/// be bad: malformed, oversized, lying about its size, or reaching outside
/// itself.
/// </summary>
public class ThemePackageLoaderTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "solo-theme-tests", Guid.NewGuid().ToString("N"));

    private const string MinimalManifest = """
        { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
          "dark": { "primary": "#ff0000" } }
        """;

    public ThemePackageLoaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Test litter in the temp directory isn't worth failing a run over.
        }
    }

    [Fact]
    public void A_minimal_package_loads()
    {
        var path = WriteZip(("theme.json", Encoding.UTF8.GetBytes(MinimalManifest)));

        var package = ThemePackageLoader.LoadZip(path);

        Assert.Equal("test-theme", package.Id);
        Assert.Equal("Test Theme", package.Manifest.Name);
        Assert.False(package.Manifest.IsBuiltIn);
        Assert.NotEmpty(package.ContentHash);
    }

    [Fact]
    public void Content_urls_carry_the_package_hash()
    {
        var manifest = """
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "stylesheet": "content/theme.css" }
            """;
        var path = WriteZip(
            ("theme.json", Encoding.UTF8.GetBytes(manifest)),
            ("content/theme.css", Encoding.UTF8.GetBytes(":root { color: red; }")));

        var package = ThemePackageLoader.LoadZip(path);
        var served = package.ToServedDto();

        Assert.Equal($"api/themes/test-theme/{package.ContentHash}/content/theme.css", served.Stylesheet);
    }

    [Fact]
    public void Changing_a_content_file_changes_the_hash()
    {
        var first = ThemePackageLoader.LoadZip(WriteZip("a.zip",
            ("theme.json", Encoding.UTF8.GetBytes(MinimalManifest)),
            ("content/theme.css", Encoding.UTF8.GetBytes("a{}"))));
        var second = ThemePackageLoader.LoadZip(WriteZip("b.zip",
            ("theme.json", Encoding.UTF8.GetBytes(MinimalManifest)),
            ("content/theme.css", Encoding.UTF8.GetBytes("b{}"))));

        Assert.NotEqual(first.ContentHash, second.ContentHash);
    }

    [Theory]
    [InlineData("../escape.css")]
    [InlineData("content/../../escape.css")]
    [InlineData("/etc/passwd.css")]
    [InlineData("..\\escape.css")]
    public void Entries_that_escape_the_package_are_refused(string entryName)
    {
        var path = WriteZip(
            ("theme.json", Encoding.UTF8.GetBytes(MinimalManifest)),
            (entryName, Encoding.UTF8.GetBytes("body{}")));

        var error = Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(path));
        Assert.Contains("unsafe path", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Executable_content_is_refused()
    {
        var path = WriteZip(
            ("theme.json", Encoding.UTF8.GetBytes(MinimalManifest)),
            ("content/evil.js", Encoding.UTF8.GetBytes("alert(1)")));

        Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(path));
    }

    [Fact]
    public void Content_outside_the_content_folder_is_refused()
    {
        var path = WriteZip(
            ("theme.json", Encoding.UTF8.GetBytes(MinimalManifest)),
            ("theme.css", Encoding.UTF8.GetBytes("body{}")));

        Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(path));
    }

    [Fact]
    public void An_archive_that_expands_past_the_ceiling_is_refused()
    {
        // Highly compressible, so the zip on disk stays small while the declared
        // expansion runs past the total limit — the bomb shape.
        var block = new byte[ThemePackageLoader.MaxEntryBytes - 1024];
        var entries = new List<(string, byte[])> { ("theme.json", Encoding.UTF8.GetBytes(MinimalManifest)) };
        for (var i = 0; i < 6; i++)
        {
            entries.Add(($"content/pad{i}.css", block));
        }

        var error = Assert.Throws<ThemePackageException>(
            () => ThemePackageLoader.LoadZip(WriteZip("bomb.zip", [.. entries])));
        Assert.Contains("expands to more than", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_package_without_a_manifest_is_refused()
    {
        var path = WriteZip(("content/theme.css", Encoding.UTF8.GetBytes("body{}")));

        var error = Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(path));
        Assert.Contains("theme.json", error.Message);
    }

    [Fact]
    public void A_file_that_is_not_a_zip_is_refused()
    {
        var path = Path.Combine(_directory, "broken.zip");
        File.WriteAllText(path, "this is not a zip");

        Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(path));
    }

    [Theory]
    [InlineData("red; } body { display: none")]
    [InlineData("url(https://evil.example/leak)")]
    [InlineData("#fff /* comment */")]
    [InlineData("var(--anything)")]
    public void Palette_values_that_are_not_colours_are_refused(string value)
    {
        var manifest = $$"""
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "dark": { "primary": "{{value}}" } }
            """;
        var path = WriteZip(("theme.json", Encoding.UTF8.GetBytes(manifest)));

        var error = Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(path));
        Assert.Contains("valid CSS colour", error.Message);
    }

    [Theory]
    [InlineData("#fff600", "#000000")]  // light fill wants dark text
    [InlineData("#1a1a1a", "#ffffff")]  // dark fill wants light text
    public void Contrast_text_is_derived_when_the_theme_omits_it(string primary, string expected)
    {
        var manifest = $$"""
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "dark": { "primary": "{{primary}}" } }
            """;

        var package = ThemePackageLoader.LoadZip(WriteZip(("theme.json", Encoding.UTF8.GetBytes(manifest))));

        Assert.Equal(expected, package.Manifest.Dark!.PrimaryContrastText);
    }

    [Fact]
    public void An_explicit_contrast_text_is_left_alone()
    {
        var manifest = """
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "dark": { "primary": "#fff600", "primaryContrastText": "#123456" } }
            """;

        var package = ThemePackageLoader.LoadZip(WriteZip(("theme.json", Encoding.UTF8.GetBytes(manifest))));

        Assert.Equal("#123456", package.Manifest.Dark!.PrimaryContrastText);
    }

    [Fact]
    public void A_login_image_that_is_not_in_the_package_is_refused()
    {
        var manifest = """
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "login": { "background": { "image": "content/missing.png" } } }
            """;
        var path = WriteZip(("theme.json", Encoding.UTF8.GetBytes(manifest)));

        var error = Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(path));
        Assert.Contains("not a content file", error.Message);
    }

    [Fact]
    public void Scrim_is_clamped_to_the_zero_to_one_range()
    {
        var manifest = """
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "login": { "background": { "image": "content/bg.png", "scrim": 4.2 } } }
            """;
        var path = WriteZip(
            ("theme.json", Encoding.UTF8.GetBytes(manifest)),
            ("content/bg.png", [0x89, (byte)'P', (byte)'N', (byte)'G']));

        var package = ThemePackageLoader.LoadZip(path);

        Assert.Equal(1, package.Manifest.Login!.Background!.Scrim);
    }

    [Theory]
    [InlineData("Not A Slug")]
    [InlineData("UPPERCASE")]
    [InlineData("")]
    [InlineData("a")]
    public void An_unusable_theme_id_is_refused(string id)
    {
        var manifest = $$"""
            { "formatVersion": 1, "id": "{{id}}", "name": "Test Theme" }
            """;
        var path = WriteZip(("theme.json", Encoding.UTF8.GetBytes(manifest)));

        Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(path));
    }

    [Fact]
    public void A_future_format_version_is_refused_rather_than_guessed_at()
    {
        var manifest = """
            { "formatVersion": 99, "id": "test-theme", "name": "Test Theme" }
            """;
        var path = WriteZip(("theme.json", Encoding.UTF8.GetBytes(manifest)));

        var error = Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(path));
        Assert.Contains("formatVersion", error.Message);
    }

    /// <summary>
    /// MudBlazor quotes any family name containing a space when it writes the
    /// CSS variable. A manifest that quotes them too yields <c>''Segoe UI''</c>,
    /// which is invalid — and the failure mode is silent: the whole font-family
    /// declaration becomes invalid at computed-value time and the theme's
    /// typography just doesn't apply.
    /// </summary>
    [Fact]
    public void Quoted_font_names_are_unquoted_rather_than_double_quoted()
    {
        var manifest = """
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "typography": {
                "fontFamily": ["'Segoe UI'", "\"Courier New\"", "sans-serif"],
                "headingFontFamily": ["'IBM Plex Mono'", "monospace"]
              } }
            """;

        var package = ThemePackageLoader.LoadZip(WriteZip(("theme.json", Encoding.UTF8.GetBytes(manifest))));

        Assert.Equal(["Segoe UI", "Courier New", "sans-serif"], package.Manifest.Typography!.FontFamily);
        Assert.Equal(["IBM Plex Mono", "monospace"], package.Manifest.Typography.HeadingFontFamily);
    }

    [Fact]
    public void A_font_name_that_could_break_out_of_the_declaration_is_refused()
    {
        var manifest = """
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "typography": { "fontFamily": ["sans-serif; } body { display: none"] } }
            """;

        var error = Assert.Throws<ThemePackageException>(
            () => ThemePackageLoader.LoadZip(WriteZip(("theme.json", Encoding.UTF8.GetBytes(manifest)))));
        Assert.Contains("font family name", error.Message);
    }

    [Fact]
    public void A_packaged_font_becomes_a_generated_font_face_stylesheet()
    {
        var manifest = """
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "fonts": [
                { "family": "Test Sans", "src": "content/fonts/test.woff2",
                  "weight": "100 900", "style": "normal", "display": "swap" }
              ],
              "typography": { "fontFamily": ["Test Sans", "sans-serif"] } }
            """;
        var package = ThemePackageLoader.LoadZip(WriteZip(
            ("theme.json", Encoding.UTF8.GetBytes(manifest)),
            ("content/fonts/test.woff2", Encoding.UTF8.GetBytes("wOF2 stub"))));

        var css = Encoding.UTF8.GetString(package.Content[ThemePackageLoader.FontStylesheetName].Bytes);

        Assert.Contains("font-family: 'Test Sans';", css);
        Assert.Contains("src: url('content/fonts/test.woff2') format('woff2');", css);
        Assert.Contains("font-weight: 100 900;", css);
        Assert.Contains("font-display: swap;", css);

        // Served from the package root, so the relative url() above resolves to
        // the font's own hashed URL.
        var served = package.ToServedDto();
        Assert.Equal($"api/themes/test-theme/{package.ContentHash}/fonts.css", served.FontStylesheet);
    }

    [Fact]
    public void Defaults_are_filled_in_for_a_minimally_declared_font()
    {
        var manifest = """
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "fonts": [ { "family": "Test Sans", "src": "content/test.otf" } ] }
            """;
        var package = ThemePackageLoader.LoadZip(WriteZip(
            ("theme.json", Encoding.UTF8.GetBytes(manifest)),
            ("content/test.otf", Encoding.UTF8.GetBytes("OTTO stub"))));

        var css = Encoding.UTF8.GetString(package.Content[ThemePackageLoader.FontStylesheetName].Bytes);

        Assert.Contains("format('opentype')", css);
        Assert.Contains("font-weight: normal;", css);
        Assert.Contains("font-style: normal;", css);
        // swap, so the app is readable in a fallback face rather than blank.
        Assert.Contains("font-display: swap;", css);
    }

    [Fact]
    public void A_theme_without_fonts_generates_no_stylesheet()
    {
        var package = ThemePackageLoader.LoadZip(WriteZip(("theme.json", Encoding.UTF8.GetBytes(MinimalManifest))));

        Assert.Null(package.Manifest.FontStylesheet);
        Assert.Null(package.ToServedDto().FontStylesheet);
        Assert.DoesNotContain(ThemePackageLoader.FontStylesheetName, package.Content.Keys);
    }

    [Fact]
    public void A_font_pointing_at_a_missing_file_is_refused()
    {
        var manifest = """
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "fonts": [ { "family": "Test Sans", "src": "content/nope.woff2" } ] }
            """;

        var error = Assert.Throws<ThemePackageException>(
            () => ThemePackageLoader.LoadZip(WriteZip(("theme.json", Encoding.UTF8.GetBytes(manifest)))));
        Assert.Contains("not a content file", error.Message);
    }

    [Fact]
    public void A_font_src_that_is_not_a_font_is_refused()
    {
        var manifest = """
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "fonts": [ { "family": "Test Sans", "src": "content/logo.png" } ] }
            """;

        var error = Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(WriteZip(
            ("theme.json", Encoding.UTF8.GetBytes(manifest)),
            ("content/logo.png", [0x89, (byte)'P', (byte)'N', (byte)'G']))));
        Assert.Contains("not a font file", error.Message);
    }

    [Theory]
    [InlineData("weight", "400; } body { display: none")]
    [InlineData("style", "normal'); } body { display:none; @font-face { src: url('x")]
    [InlineData("display", "swap} body{display:none")]
    public void Font_values_that_could_break_out_of_the_generated_css_are_refused(string field, string value)
    {
        var manifest = $$"""
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "fonts": [ { "family": "Test Sans", "src": "content/test.woff2", "{{field}}": "{{value}}" } ] }
            """;

        Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(WriteZip(
            ("theme.json", Encoding.UTF8.GetBytes(manifest.Replace("\\", "\\\\"))),
            ("content/test.woff2", Encoding.UTF8.GetBytes("wOF2 stub")))));
    }

    /// <summary>
    /// A quote in a filename would close the <c>url('…')</c> in the generated
    /// stylesheet, so the character set is restricted at the path level — which
    /// covers every emitter, not just this one.
    /// </summary>
    /// <remarks>
    /// Backslashes are deliberately absent: they are normalized to path
    /// separators, because that is how a zip built on Windows spells them.
    /// </remarks>
    [Theory]
    [InlineData("content/ev'il.woff2")]
    [InlineData("content/a file.png")]
    [InlineData("content/quote\"d.png")]
    public void Content_paths_with_characters_that_need_escaping_are_refused(string entryName)
    {
        var path = WriteZip(
            ("theme.json", Encoding.UTF8.GetBytes(MinimalManifest)),
            (entryName, Encoding.UTF8.GetBytes("x")));

        Assert.Throws<ThemePackageException>(() => ThemePackageLoader.LoadZip(path));
    }

    [Fact]
    public void Fit_modes_round_trip_by_name()
    {
        var manifest = """
            { "formatVersion": 1, "id": "test-theme", "name": "Test Theme",
              "login": { "background": { "image": "content/bg.png", "fit": "height" } } }
            """;
        var path = WriteZip(
            ("theme.json", Encoding.UTF8.GetBytes(manifest)),
            ("content/bg.png", [0x89, (byte)'P', (byte)'N', (byte)'G']));

        var package = ThemePackageLoader.LoadZip(path);

        Assert.Equal(ThemeBackgroundFit.Height, package.Manifest.Login!.Background!.Fit);
    }

    private string WriteZip(params (string Name, byte[] Bytes)[] entries) => WriteZip("theme.zip", entries);

    private string WriteZip(string fileName, params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_directory, fileName);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var entryStream = entry.Open();
            entryStream.Write(bytes);
        }

        return path;
    }
}
