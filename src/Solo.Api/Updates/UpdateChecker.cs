using Microsoft.Extensions.Options;
using Solo.Extensibility;
using Solo.Extensions;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Api.Updates;

/// <summary>
/// Answers "is anything out of date?" for the server and for each installed
/// extension.
/// </summary>
/// <remarks>
/// <para>Server-side, and it has to be: the client's CSP is
/// <c>connect-src 'self' https://accounts.google.com</c>
/// (<see cref="SecurityHeaders"/>), so the browser cannot fetch a manifest from
/// GitHub at all. Checking here also means one fetch per instance rather than
/// one per open tab.</para>
///
/// <para>Two feeds can name an extension. The host's own manifest carries
/// entries for the extensions that ship with a Solo release, and a package may
/// name its own feed in <c>extension.json</c>. The host's manifest wins for a
/// package it describes, because those two versions are released together and
/// an entry in the host feed is the one that was tested against this
/// server.</para>
/// </remarks>
public sealed class UpdateChecker(
    ReleaseFeedClient feeds,
    ExtensionState extensions,
    IOptions<UpdateOptions> options,
    ILogger<UpdateChecker> logger)
{
    private readonly UpdateOptions _options = options.Value;

    public async Task<UpdateStatusDto> GetStatusAsync(bool force, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return new UpdateStatusDto(Enabled: false, null, null, null, []);
        }

        var host = await feeds.GetAsync(_options.ManifestUrl, force, ct);
        var server = host.Feed is null ? null : ServerUpdate(host.Feed);
        var extensionUpdates = await ExtensionUpdatesAsync(host, force, ct);

        return new UpdateStatusDto(
            Enabled: true,
            host.FetchedAt,
            host.Error,
            server,
            extensionUpdates);
    }

    private ServerUpdateDto? ServerUpdate(ReleaseFeed feed)
    {
        var available = feed.Server?.Version;
        if (!SemanticVersion.IsNewer(available, SoloVersion.Server))
        {
            return null;
        }

        // The release that holds the server's assets, not the release the
        // manifest itself came from: a train that didn't rebuild the server
        // carries its entry forward, and the operator wants the notes for the
        // release that actually changed it.
        var tag = feed.Server?.Release ?? feed.Release?.Tag;
        var url = string.IsNullOrWhiteSpace(tag)
            ? _options.ReleasesUrl
            : $"{_options.ReleasesUrl.TrimEnd('/')}/tag/{Uri.EscapeDataString(tag)}";

        return new ServerUpdateDto(
            SoloVersion.Server,
            available!,
            url,
            feed.Server?.Image,
            feed.Release?.Published);
    }

    private async Task<List<ExtensionUpdateDto>> ExtensionUpdatesAsync(
        FeedResult host, bool force, CancellationToken ct)
    {
        var updates = new List<ExtensionUpdateDto>();

        foreach (var package in extensions.Packages)
        {
            var entry = Entry(host.Feed, package.Id);

            // Only ask the package's own feed when the host's doesn't describe
            // it. A first-party extension therefore costs no extra request.
            if (entry is null && !string.IsNullOrWhiteSpace(package.Manifest.Updates))
            {
                var own = await feeds.GetAsync(package.Manifest.Updates!, force, ct);
                if (own.Error is not null)
                {
                    logger.LogInformation(
                        "Update feed for extension {Id} could not be read: {Error}",
                        package.Id, own.Error);
                }

                entry = Entry(own.Feed, package.Id);
            }

            if (entry?.Version is null || !SemanticVersion.IsNewer(entry.Version, package.Manifest.Version))
            {
                continue;
            }

            // Nothing can be installed without these, so an entry missing them
            // is not an update, it is a malformed feed.
            if (string.IsNullOrWhiteSpace(entry.Url) || string.IsNullOrWhiteSpace(entry.Sha512))
            {
                logger.LogInformation(
                    "Ignoring update {Version} for extension {Id}: the feed entry has no url or sha512.",
                    entry.Version, package.Id);
                continue;
            }

            var compatible = IsCompatible(entry, out var incompatibility);
            updates.Add(new ExtensionUpdateDto(
                package.Id,
                package.Manifest.Name,
                package.Manifest.Version,
                entry.Version,
                entry.Size,
                compatible,
                incompatibility));
        }

        return updates;
    }

    private static ReleaseFeedExtension? Entry(ReleaseFeed? feed, string id) =>
        feed?.Extensions.FirstOrDefault(e =>
            string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this instance satisfies the new package's declared ranges —
    /// <em>both</em> of them.
    /// </summary>
    /// <remarks>
    /// <para>Checked before the update is offered, not after it is installed.
    /// An out-of-range package is refused at the next boot and reported as a
    /// load failure — which, to whoever clicked Update, is indistinguishable
    /// from the update having broken the extension.</para>
    ///
    /// <para><c>soloClientVersion</c> matters as much as <c>soloApiVersion</c>
    /// here and is easy to forget, because the two fail differently.
    /// A server half out of range never loads, loudly. A <em>client</em> half
    /// out of range loads on the server and is then skipped in the browser, so
    /// the extension keeps working except that its pages are gone — reported on
    /// this very page as "Client pages unavailable". Offering an update that
    /// does that is worse than not offering one.</para>
    ///
    /// <para>The client range is compared against this build's contract, which
    /// is what the hosted client ships with. A desktop shell older than the
    /// instance bundles its own, older client and may still skip the pages; the
    /// server cannot know the shell's version, and the shell already warns when
    /// it is behind.</para>
    ///
    /// <para>An unparseable or absent range is treated as compatible, matching
    /// what the loader does with one.</para>
    /// </remarks>
    private static bool IsCompatible(ReleaseFeedExtension entry, out string? reason)
    {
        if (!Satisfies(entry.SoloApiVersion, out var serverRange))
        {
            reason = $"Version {entry.Version} needs a Solo server in the range {serverRange}; " +
                     $"this one implements {ContractVersion.Current}. Update Solo first.";
            return false;
        }

        if (!Satisfies(entry.SoloClientVersion, out var clientRange))
        {
            reason = $"Version {entry.Version} needs a Solo client in the range {clientRange}; " +
                     $"this one is {ContractVersion.Current}, so its pages would not load. " +
                     "Update Solo first.";
            return false;
        }

        reason = null;
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
}
