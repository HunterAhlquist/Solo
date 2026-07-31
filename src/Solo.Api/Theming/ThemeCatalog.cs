using Microsoft.Extensions.Options;
using Solo.Shared.Dtos;

namespace Solo.Api.Theming;

public sealed class ThemeOptions
{
    public const string Section = "Themes";

    /// <summary>
    /// Directory scanned for <c>*.zip</c> theme packages — a mounted volume in a
    /// deployment, so themes survive an image update the way the database does.
    /// Missing or empty means built-in themes only.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>Theme used when the instance hasn't chosen one, or chose one that isn't installed.</summary>
    public string DefaultThemeId { get; set; } = "ahlquist-interactive";
}

/// <summary>
/// Every theme this instance can serve: the ones built into the image, plus
/// whatever packages are sitting in the themes volume.
/// </summary>
/// <remarks>
/// <para>Held in memory and swapped as a whole. Packages are small and few, and
/// reading them once means a half-written zip — an operator mid-copy — can never
/// be served: it either loads completely or is reported as a failure and the
/// previous snapshot stays live.</para>
///
/// <para>Rescanning is explicit (startup, or the admin's Rescan button) rather
/// than watched, so dropping in a theme doesn't race a file copy.</para>
/// </remarks>
public sealed class ThemeCatalog
{
    private readonly ILogger<ThemeCatalog> _logger;
    private readonly ThemeOptions _options;
    private readonly string _builtInDirectory;
    private volatile Snapshot _snapshot = new([], []);

    /// <summary>Last configured-but-absent theme id reported; see <see cref="WarnMissing"/>.</summary>
    private string? _missingThemeId;

    public ThemeCatalog(
        IOptions<ThemeOptions> options, IWebHostEnvironment environment, ILogger<ThemeCatalog> logger)
    {
        _options = options.Value;
        _logger = logger;
        _builtInDirectory = Path.Combine(environment.ContentRootPath, "Themes");
        Rescan();
    }

    public IReadOnlyList<ThemePackage> Themes => _snapshot.Themes;

    public IReadOnlyList<ThemeLoadFailure> Failures => _snapshot.Failures;

    /// <summary>
    /// The theme for a given instance setting. Falls back to the default for an
    /// unset id and for one naming a package that is no longer installed —
    /// deleting a zip must not leave the app unable to render.
    /// </summary>
    public ThemePackage Resolve(string? themeId)
    {
        var themes = _snapshot.Themes;
        var requested = Find(themes, themeId);
        if (requested is not null)
        {
            // Back after being gone (the operator restored the package): forget
            // the warning so a second disappearance is reported again.
            Interlocked.Exchange(ref _missingThemeId, null);
            return requested;
        }

        if (!string.IsNullOrWhiteSpace(themeId))
        {
            WarnMissing(themeId);
        }

        return Find(themes, _options.DefaultThemeId)
            ?? themes.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No themes are installed; the built-in themes are missing from the deployment.");

        static ThemePackage? Find(IReadOnlyList<ThemePackage> themes, string? id) =>
            string.IsNullOrWhiteSpace(id)
                ? null
                : themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reports a configured theme that is no longer installed — once per
    /// disappearance, not once per request.
    /// </summary>
    /// <remarks>
    /// This resolves on every client bootstrap, so logging unconditionally would
    /// bury the log under one line per page load. Warning on the transition
    /// makes it a single entry an operator can actually find, which is the whole
    /// point of recording it.
    /// </remarks>
    private void WarnMissing(string themeId)
    {
        if (Interlocked.Exchange(ref _missingThemeId, themeId) == themeId)
        {
            return;
        }

        _logger.LogError(
            "Theme '{ThemeId}' is selected for this instance but is not installed — serving '{Fallback}' instead. "
            + "Restore its package in the themes directory and rescan, or pick another theme in Admin settings.",
            themeId,
            _options.DefaultThemeId);
    }

    /// <summary>
    /// Looks up a content file. The hash must match the package currently
    /// loaded: content URLs are served <c>immutable</c>, so an id+path under a
    /// stale hash is a different package's file and must 404 rather than
    /// silently serve the new bytes under the old URL.
    /// </summary>
    public bool TryGetContent(string themeId, string hash, string path, out ThemeContent content)
    {
        content = null!;
        var theme = _snapshot.Themes.FirstOrDefault(
            t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase));

        return theme is not null
            && string.Equals(theme.ContentHash, hash, StringComparison.OrdinalIgnoreCase)
            && theme.Content.TryGetValue(path, out content!);
    }

    /// <summary>Reloads built-ins and the themes volume, replacing the snapshot only once it's complete.</summary>
    public void Rescan()
    {
        var themes = new List<ThemePackage>();
        var failures = new List<ThemeLoadFailure>();

        LoadBuiltIns(themes, failures);
        LoadVolume(themes, failures);

        _snapshot = new Snapshot(themes, failures);
        _logger.LogInformation(
            "Theme catalog loaded: {Count} theme(s), {Failures} failure(s).", themes.Count, failures.Count);
    }

    private void LoadBuiltIns(List<ThemePackage> themes, List<ThemeLoadFailure> failures)
    {
        if (!Directory.Exists(_builtInDirectory))
        {
            _logger.LogError("Built-in themes are missing from {Directory}.", _builtInDirectory);
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(_builtInDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            Add(themes, failures, Path.GetFileName(directory),
                () => ThemePackageLoader.LoadDirectory(directory, isBuiltIn: true));
        }
    }

    private void LoadVolume(List<ThemePackage> themes, List<ThemeLoadFailure> failures)
    {
        if (string.IsNullOrWhiteSpace(_options.Directory))
        {
            return;
        }

        if (!Directory.Exists(_options.Directory))
        {
            // Not an error: a deployment without a themes volume is the normal case.
            _logger.LogInformation("No themes directory at {Directory}; built-in themes only.", _options.Directory);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_options.Directory, "*.zip").OrderBy(f => f, StringComparer.Ordinal))
        {
            Add(themes, failures, Path.GetFileName(file), () => ThemePackageLoader.LoadZip(file));
        }
    }

    private void Add(
        List<ThemePackage> themes, List<ThemeLoadFailure> failures, string name, Func<ThemePackage> load)
    {
        try
        {
            var package = load();

            // First one wins, so a dropped-in package can never shadow a built-in
            // id and change what the instance's saved theme means underneath it.
            if (themes.Any(t => string.Equals(t.Id, package.Id, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(new ThemeLoadFailure(name, $"Theme id '{package.Id}' is already installed."));
                return;
            }

            themes.Add(package);
        }
        catch (Exception e) when (e is ThemePackageException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Theme package {Name} was rejected: {Message}", name, e.Message);
            failures.Add(new ThemeLoadFailure(name, e.Message));
        }
    }

    public ThemeScanResultDto ToScanResult(string? activeThemeId)
    {
        var active = Resolve(activeThemeId).Id;
        return new ThemeScanResultDto(
            [.. _snapshot.Themes.Select(t => t.ToSummary(t.Id == active))],
            [.. _snapshot.Failures.Select(f => new ThemeScanErrorDto(f.File, f.Message))]);
    }

    private sealed record Snapshot(IReadOnlyList<ThemePackage> Themes, IReadOnlyList<ThemeLoadFailure> Failures);
}
