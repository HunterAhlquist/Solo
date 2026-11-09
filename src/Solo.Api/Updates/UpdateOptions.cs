namespace Solo.Api.Updates;

/// <summary>
/// Where update checks look and whether they happen at all.
/// </summary>
public sealed class UpdateOptions
{
    public const string Section = "Updates";

    /// <summary>
    /// Whether this instance checks for updates. On by default.
    /// </summary>
    /// <remarks>
    /// The check is an unauthenticated GET of a public JSON file and sends
    /// nothing about the instance — no version, no identifier, no telemetry.
    /// It is still an outbound call to a third party, which some deployments
    /// will not permit, so <c>Updates__Enabled=false</c> turns it off and the
    /// UI then says checks are disabled rather than claiming the instance is
    /// current.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The release manifest to read, when set. A fork or an air-gapped mirror
    /// points this at its own copy. Unset, <see cref="ManifestUrlFor"/> picks
    /// one by the server's own version.
    /// </summary>
    public string? ManifestUrl { get; set; }

    /// <summary>
    /// The manifest a server of this version reads: <see cref="ManifestUrl"/>
    /// when set; for a preview build, the manifest of its own preview release;
    /// otherwise the newest stable release's.
    /// </summary>
    /// <remarks>
    /// <para>The <c>releases/latest/download/</c> redirect resolves to the
    /// newest non-prerelease release, so a stable instance follows the release
    /// train without naming a version, and is never told to update to a
    /// preview.</para>
    ///
    /// <para>A preview reads its own release instead, because that is where
    /// the extension packages built with it are: on the stable feed it would
    /// be offered only the older stable ones, and an operator had to pin
    /// <see cref="ManifestUrl"/> to each preview tag by hand. Not "the newest
    /// prerelease": other branches' previews share this repository, so that
    /// could hand one branch's instance another's packages. The cost is that a
    /// preview hears about neither newer previews nor a later stable release;
    /// moving it to one is a new image either way. A prerelease cut from a
    /// <c>v</c> tag (<c>v1.2.0-rc</c>) has no <c>preview-v</c> release, so it
    /// needs <see cref="ManifestUrl"/> set.</para>
    /// </remarks>
    public string ManifestUrlFor(string serverVersion)
    {
        if (!string.IsNullOrWhiteSpace(ManifestUrl))
        {
            return ManifestUrl;
        }

        // The informational version carries the commit as build metadata
        // ("1.21.0-mobile.3+abc123"), which the tag doesn't.
        var version = serverVersion.Split('+')[0];
        return version.Contains('-')
            ? $"{RepositoryReleases}/download/preview-v{Uri.EscapeDataString(version)}/release-manifest.json"
            : $"{RepositoryReleases}/latest/download/release-manifest.json";
    }

    private const string RepositoryReleases = "https://github.com/HunterAhlquist/Solo/releases";

    /// <summary>
    /// Where a found update sends the operator. The release the feed names is
    /// appended when it names one.
    /// </summary>
    public string ReleasesUrl { get; set; } = RepositoryReleases;

    /// <summary>
    /// How long a fetched manifest is reused. An update is not urgent and the
    /// feed is a third party's bandwidth, so this is hours rather than minutes.
    /// </summary>
    public TimeSpan CacheFor { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How long to wait on the feed. Short: a slow or black-holed request must
    /// not hold the admin page, and the answer is only ever advisory.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Largest extension package that will be downloaded, as a guard against a
    /// feed that names an implausible asset.
    /// </summary>
    public long MaxPackageBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// How long a package download may take. Far longer than
    /// <see cref="Timeout"/> — this is megabytes, not a few hundred bytes — but
    /// finite, because the handler's own timeout is disabled so that reading a
    /// feed and downloading a package can be bounded differently.
    /// </summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public const string HttpClient = "updates";
}
