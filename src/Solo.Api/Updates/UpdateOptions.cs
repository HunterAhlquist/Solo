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
    /// The release manifest to read. A fork or an air-gapped mirror points this
    /// at its own copy.
    /// </summary>
    /// <remarks>
    /// The <c>releases/latest/download/</c> redirect resolves to the newest
    /// non-prerelease release, so this URL follows the release train without
    /// naming a version. Previews are skipped by that redirect deliberately —
    /// an instance must not be told to update to a prerelease.
    /// </remarks>
    public string ManifestUrl { get; set; } =
        "https://github.com/HunterAhlquist/Solo/releases/latest/download/release-manifest.json";

    /// <summary>
    /// Where a found update sends the operator. The release the feed names is
    /// appended when it names one.
    /// </summary>
    public string ReleasesUrl { get; set; } = "https://github.com/HunterAhlquist/Solo/releases";

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
