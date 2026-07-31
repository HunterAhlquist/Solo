using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solo.Api.Theming;

namespace Solo.Api.Tests;

/// <summary>
/// What happens when the instance's chosen theme isn't there. Deleting a package
/// is a thing operators do, and the app has to keep rendering.
/// </summary>
public class ThemeCatalogTests
{
    [Fact]
    public void A_theme_that_is_not_installed_falls_back_to_the_default()
    {
        var catalog = Build();

        var resolved = catalog.Resolve("deleted-theme");

        Assert.Equal("ahlquist-interactive", resolved.Id);
    }

    [Fact]
    public void No_configured_theme_resolves_to_the_default()
    {
        var catalog = Build();

        Assert.Equal("ahlquist-interactive", catalog.Resolve(null).Id);
        Assert.Equal("ahlquist-interactive", catalog.Resolve("").Id);
    }

    [Fact]
    public void An_installed_theme_resolves_to_itself()
    {
        var catalog = Build();

        Assert.Equal("blueprint", catalog.Resolve("blueprint").Id);
        // Ids are matched case-insensitively; a hand-edited setting shouldn't
        // silently reset the instance's branding.
        Assert.Equal("blueprint", catalog.Resolve("BluePrint").Id);
    }

    /// <summary>
    /// If the default itself is missing the app still has to render, so the
    /// resolution walks down to any installed theme rather than throwing.
    /// </summary>
    [Fact]
    public void A_missing_default_falls_through_to_whatever_is_installed()
    {
        var catalog = Build(defaultThemeId: "not-a-theme");

        Assert.NotNull(catalog.Resolve("also-not-a-theme"));
    }

    [Fact]
    public void Content_is_refused_under_a_stale_hash()
    {
        var catalog = Build();
        var blueprint = catalog.Themes.Single(t => t.Id == "blueprint");
        var path = blueprint.Content.Keys.First();

        Assert.True(catalog.TryGetContent("blueprint", blueprint.ContentHash, path, out _));
        Assert.False(catalog.TryGetContent("blueprint", "0000000000000000", path, out _));
    }

    private static ThemeCatalog Build(string defaultThemeId = "ahlquist-interactive") =>
        new(Options.Create(new ThemeOptions { Directory = null, DefaultThemeId = defaultThemeId }),
            new StubEnvironment(FindRepositoryRoot()),
            NullLogger<ThemeCatalog>.Instance);

    /// <summary>The API project root, so the catalog finds the built-in <c>Themes/</c> beside it.</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Solo.Api");
            if (Directory.Exists(Path.Combine(candidate, "Themes")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/Solo.Api from the test output directory.");
    }

    private sealed class StubEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ContentRootPath { get; set; } = contentRootPath;
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Solo.Api.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
