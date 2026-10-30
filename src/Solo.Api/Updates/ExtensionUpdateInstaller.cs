using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Solo.Extensibility;
using Solo.Extensions;
using Solo.Shared.Extensibility;

namespace Solo.Api.Updates;

/// <summary>
/// Replaces an installed extension package with a newer one from its feed.
/// </summary>
/// <remarks>
/// <para>This downloads code and arranges for the instance to run it, so every
/// step is written to fail closed. In order: the package must have been loaded
/// from a zip in the configured directory, that directory must be writable, the
/// feed entry must carry a URL and a digest, the download must match that
/// digest and the declared size, the zip must load as a package whose id and
/// version are the ones expected, and its contract range must admit this
/// server. Only then is anything moved into place.</para>
///
/// <para>The digest is the part that matters most. The feed names where the
/// bytes come from, and an operator who installed the package already accepted
/// its author — but nothing takes the author's word for the bytes themselves,
/// and nothing is ever written into the extensions directory before the hash
/// has been checked. The staging file is in that same directory so the final
/// move is a rename on one filesystem rather than a copy that can be
/// interrupted halfway.</para>
///
/// <para>The previous package is kept as <c>.zip.bak</c> rather than deleted.
/// The loader only scans <c>*.zip</c>, so a backup is inert, and a bad update
/// is then recoverable by an operator with shell access alone. The old zip is
/// also <em>moved</em> and not left in place: two zips declaring the same id
/// make the loader reject the second as a duplicate, which would present as the
/// extension vanishing.</para>
/// </remarks>
public sealed class ExtensionUpdateInstaller(
    ReleaseFeedClient feeds,
    ExtensionState extensions,
    IHttpClientFactory factory,
    IOptions<UpdateOptions> updateOptions,
    IOptions<ExtensionOptions> extensionOptions,
    ILogger<ExtensionUpdateInstaller> logger)
{
    private readonly UpdateOptions _options = updateOptions.Value;
    private readonly ExtensionOptions _extensions = extensionOptions.Value;

    public async Task<InstallOutcome> InstallAsync(string extensionId, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return InstallOutcome.Refused("Update checks are disabled on this instance.");
        }

        var package = extensions.Find(extensionId);
        if (package is null)
        {
            return InstallOutcome.NotFound();
        }

        var directory = _extensions.Directory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return InstallOutcome.Refused(
                "This instance has no extensions directory configured, so there is nowhere to " +
                "install a package. Set Extensions__Directory.");
        }

        // A package loaded unzipped from a development directory has no zip to
        // replace, and overwriting a developer's working tree is not something
        // this should do.
        if (!package.Source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return InstallOutcome.Refused(
                $"{extensionId} was loaded from '{package.Source}', not a package in the extensions " +
                "directory. Updates apply only to installed .zip packages.");
        }

        var current = Path.Combine(directory, package.Source);
        if (!File.Exists(current))
        {
            return InstallOutcome.Refused(
                $"The package '{package.Source}' is no longer in the extensions directory.");
        }

        if (!CanWrite(directory, out var writeError))
        {
            // Worth its own message: the usual cause is a host-path mount owned
            // by a different UID, and "permission denied" alone sends an
            // operator looking in the wrong place.
            return InstallOutcome.Refused(
                $"The extensions directory is not writable by this instance ({writeError}). " +
                "It runs as UID 1654; if the directory is a host path, make it writable by that user.");
        }

        var entry = await ResolveAsync(package, ct);
        if (entry is null)
        {
            return InstallOutcome.Refused(
                $"No feed names an update for {extensionId}.");
        }

        if (!SemanticVersion.IsNewer(entry.Version, package.Manifest.Version))
        {
            return InstallOutcome.Refused(
                $"{extensionId} {package.Manifest.Version} is already current.");
        }

        if (string.IsNullOrWhiteSpace(entry.Url) || string.IsNullOrWhiteSpace(entry.Sha512))
        {
            return InstallOutcome.Refused(
                $"The feed entry for {extensionId} {entry.Version} has no download URL or digest.");
        }

        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return InstallOutcome.Refused($"'{entry.Url}' is not an https URL.");
        }

        var staging = Path.Combine(directory, $".solo-update-{extensionId}-{Guid.NewGuid():n}.tmp");
        try
        {
            var downloaded = await DownloadAsync(uri, entry, staging, ct);
            if (downloaded is not null)
            {
                return InstallOutcome.Refused(downloaded);
            }

            if (!Verify(staging, entry, out var verifyError))
            {
                return InstallOutcome.Refused(verifyError!);
            }

            if (!Validate(staging, extensionId, entry, out var validateError))
            {
                return InstallOutcome.Refused(validateError!);
            }

            // The asset name carries the version, so the new file is a new name
            // and the old one has to go. Both moves are renames within one
            // directory.
            var installed = Path.Combine(directory, SafeAssetName(entry, extensionId));
            var backup = BackupPath(directory, extensionId);

            // Named for the extension and not for the package it holds, which is
            // the point: the previous name carried the version, so every update
            // minted a *new* .bak and none was ever replaced — leaving one
            // multi-megabyte copy of every version ever installed in the volume,
            // and no way to tell which of them was the rollback. One slot per
            // extension, overwritten, holding the package immediately replaced.
            File.Move(current, backup, overwrite: true);
            try
            {
                File.Move(staging, installed);
            }
            catch
            {
                // Put the old package back rather than leaving the instance with
                // no copy of this extension at all.
                File.Move(backup, current);
                throw;
            }

            logger.LogWarning(
                "Extension {Id} updated from {From} to {To}. Previous package kept at {Backup}.",
                extensionId, package.Manifest.Version, entry.Version, Path.GetFileName(backup));

            return InstallOutcome.Installed(entry.Version!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            logger.LogError(e, "Installing the update for extension {Id} failed.", extensionId);
            return InstallOutcome.Refused($"Installing the update failed: {e.Message}");
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>The feed entry to install, host manifest first — see <see cref="UpdateChecker"/>.</summary>
    private async Task<ReleaseFeedExtension?> ResolveAsync(ExtensionPackage package, CancellationToken ct)
    {
        var host = await feeds.GetAsync(_options.ManifestUrl, force: false, ct);
        var entry = host.Feed?.Extensions.FirstOrDefault(e =>
            string.Equals(e.Id, package.Id, StringComparison.OrdinalIgnoreCase));

        if (entry is not null || string.IsNullOrWhiteSpace(package.Manifest.Updates))
        {
            return entry;
        }

        var own = await feeds.GetAsync(package.Manifest.Updates!, force: false, ct);
        return own.Feed?.Extensions.FirstOrDefault(e =>
            string.Equals(e.Id, package.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Streams the asset to <paramref name="staging"/>. Returns null on success
    /// or a reason to refuse.
    /// </summary>
    private async Task<string?> DownloadAsync(
        Uri uri, ReleaseFeedExtension entry, string staging, CancellationToken ct)
    {
        // The named client's own timeout is infinite so that a feed read and a
        // package download can be bounded differently; this is the bound.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.DownloadTimeout);
        ct = cts.Token;

        var client = factory.CreateClient(UpdateOptions.HttpClient);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            return $"The package could not be downloaded ({(int)response.StatusCode} " +
                   $"{response.ReasonPhrase}).";
        }

        // Checked before reading as well as after writing: Content-Length is a
        // claim, so the copy below is also bounded.
        var declared = response.Content.Headers.ContentLength;
        if (declared > _options.MaxPackageBytes)
        {
            return $"The package is {declared} bytes, over the {_options.MaxPackageBytes}-byte limit.";
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(
            staging, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > _options.MaxPackageBytes)
            {
                return $"The package exceeded the {_options.MaxPackageBytes}-byte limit while downloading.";
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return null;
    }

    private static bool Verify(string path, ReleaseFeedExtension entry, out string? error)
    {
        error = null;
        var info = new FileInfo(path);

        // A size in the feed is advisory metadata, but a mismatch means the feed
        // and the asset disagree, and the digest check below would fail anyway.
        // Checking it first produces the clearer message.
        if (entry.Size > 0 && info.Length != entry.Size)
        {
            error = $"The download is {info.Length} bytes; the feed says {entry.Size}.";
            return false;
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToBase64String(SHA512.HashData(stream));
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actual),
                System.Text.Encoding.ASCII.GetBytes(entry.Sha512!)))
        {
            error = "The download does not match the digest in the feed, so it was discarded.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Loads the downloaded zip as a package and checks it is the thing the feed
    /// promised.
    /// </summary>
    /// <remarks>
    /// The digest proves the bytes are the ones the feed named; it proves
    /// nothing about what they contain. Loading the package here means a
    /// malformed zip, a manifest for a different extension, or a version that
    /// disagrees with the feed is caught now — while the old package is still
    /// in place — rather than at the next boot, when the extension would simply
    /// be missing.
    /// </remarks>
    private static bool Validate(
        string path, string extensionId, ReleaseFeedExtension entry, out string? error)
    {
        error = null;
        ExtensionPackage candidate;
        try
        {
            candidate = ExtensionPackageLoader.LoadZip(path);
        }
        catch (Exception e) when (e is ExtensionPackageException or InvalidDataException or IOException)
        {
            error = $"The downloaded package is not a valid extension: {e.Message}";
            return false;
        }

        if (!string.Equals(candidate.Id, extensionId, StringComparison.OrdinalIgnoreCase))
        {
            error = $"The downloaded package declares id '{candidate.Id}', not '{extensionId}'.";
            return false;
        }

        if (!string.Equals(candidate.Manifest.Version, entry.Version, StringComparison.OrdinalIgnoreCase))
        {
            error = $"The downloaded package is version {candidate.Manifest.Version}, " +
                    $"but the feed offered {entry.Version}.";
            return false;
        }

        // Both ranges, off the manifest in the package rather than the feed's
        // copy of it. A client half out of range is the quiet one: it loads on
        // the server and is skipped in the browser, so the extension survives
        // the update with its pages missing.
        if (candidate.ServerAssembly is not null &&
            !Satisfies(candidate.Manifest.SoloApiVersion, out var serverRange))
        {
            error = $"{extensionId} {candidate.Manifest.Version} needs a Solo server in the range " +
                    $"{serverRange}; this one implements {ContractVersion.Current}. Update Solo first.";
            return false;
        }

        if (candidate.ClientAssembly is not null &&
            !Satisfies(candidate.Manifest.SoloClientVersion, out var clientRange))
        {
            error = $"{extensionId} {candidate.Manifest.Version} needs a Solo client in the range " +
                    $"{clientRange}; this one is {ContractVersion.Current}, so its pages would not " +
                    "load. Update Solo first.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether this build's contract falls in <paramref name="declared"/>. True
    /// when the range is absent or unparseable, as the loader treats it.
    /// </summary>
    private static bool Satisfies(string? declared, out VersionRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(declared) ||
            !VersionRange.TryParse(declared, out var parsed, out _))
        {
            return true;
        }

        range = parsed;
        return parsed.Includes(ContractVersion.Parsed);
    }

    /// <summary>
    /// The installed file name. Taken from the feed's asset name when it is a
    /// plain file name, and otherwise constructed — a feed is remote input and
    /// must not be able to choose a path.
    /// </summary>
    private static string SafeAssetName(ReleaseFeedExtension entry, string extensionId)
    {
        var asset = entry.Asset;
        if (!string.IsNullOrWhiteSpace(asset) &&
            asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(asset) == asset &&
            asset.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        {
            return asset;
        }

        return $"solo-{extensionId}-{entry.Version}.zip";
    }

    /// <summary>
    /// The one rollback slot for an extension. Not a <c>*.zip</c>, so the loader
    /// never scans it; renaming it back to a <c>.zip</c> is the manual rollback.
    /// </summary>
    /// <remarks>
    /// <para>A fixed name per extension is the whole mechanism: it is
    /// overwritten on each update, so growth is bounded without anything having
    /// to go looking for files to delete.</para>
    ///
    /// <para><b>Nothing else in the extensions directory is removed, and no
    /// pattern is swept.</b> An earlier revision of this file named the backup
    /// after the package it held and then globbed <c>solo-&lt;id&gt;-*.zip.bak</c>
    /// to tidy up after itself, which could delete two kinds of file it had no
    /// business touching. Extension ids may contain hyphens
    /// (<c>^[a-z0-9][a-z0-9-]{0,63}$</c>), so updating <c>roadmap</c> matched
    /// and deleted <c>solo-roadmap-pro-1.4.0.zip.bak</c> — a different
    /// extension's only recovery package. And that same pattern is exactly what
    /// an operator keeping a copy of a package would name it.</para>
    /// </remarks>
    private static string BackupPath(string directory, string extensionId) =>
        Path.Combine(directory, $"solo-{extensionId}.rollback.zip.bak");

    private static bool CanWrite(string directory, out string? error)
    {
        error = null;
        var probe = Path.Combine(directory, $".solo-write-probe-{Guid.NewGuid():n}");
        try
        {
            // Actually written rather than inferred from permissions: the
            // failure this guards against is a read-only mount, which the
            // permission bits on the directory do not describe.
            File.WriteAllBytes(probe, []);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
        finally
        {
            TryDelete(probe);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do: it is a temp file in a directory the
            // operator can see.
        }
    }
}

/// <param name="Found">False when no such extension is installed.</param>
/// <param name="Version">The version now staged on disk, on success.</param>
/// <param name="Error">Why the update was refused, when it was.</param>
public sealed record InstallOutcome(bool Found, string? Version, string? Error)
{
    public static InstallOutcome Installed(string version) => new(true, version, null);

    public static InstallOutcome Refused(string error) => new(true, null, error);

    public static InstallOutcome NotFound() => new(false, null, null);
}
