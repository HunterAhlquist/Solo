using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solo.Api.Updates;
using Solo.Extensibility;
using Solo.Extensions;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Xunit;

namespace Solo.Api.Tests;

/// <summary>
/// What the admin page is told. The installer refuses an incompatible package
/// too, but by then someone has clicked a button and waited for a download —
/// so the useful place to get this right is here, before it is offered.
/// </summary>
public sealed class UpdateCheckerTests
{
    private static async Task<ExtensionUpdateDto?> CheckAsync(
        string installedVersion, ReleaseFeedExtension entry, string? ownFeed = null)
    {
        var manifest = new ExtensionManifest
        {
            Id = "roadmap",
            Name = "Roadmap",
            Version = installedVersion,
            SoloApiVersion = ">=1.0 <2.0",
            SoloClientVersion = ">=1.0 <2.0",
            Server = "server/Thing.dll",
            Updates = ownFeed,
        };

        var state = new ExtensionState(
            [new ExtensionPackage { Manifest = manifest, Source = "solo-roadmap-1.0.0.zip" }], []);

        var options = new UpdateOptions { ManifestUrl = "https://host.invalid/release-manifest.json" };
        var factory = new StubFactory(new ReleaseFeed { Schema = 1, Extensions = [entry] });
        var feeds = new ReleaseFeedClient(
            factory, Options.Create(options), TimeProvider.System,
            NullLogger<ReleaseFeedClient>.Instance);

        var checker = new UpdateChecker(
            feeds, state, Options.Create(options), NullLogger<UpdateChecker>.Instance);

        var status = await checker.GetStatusAsync(force: false, CancellationToken.None);
        return status.Extensions.SingleOrDefault();
    }

    private static ReleaseFeedExtension Entry(
        string version = "1.1.0", string api = ">=1.0 <2.0", string client = ">=1.0 <2.0") => new()
    {
        Id = "roadmap",
        Version = version,
        Asset = $"solo-roadmap-{version}.zip",
        Url = $"https://feed.invalid/solo-roadmap-{version}.zip",
        Size = 1024,
        Sha512 = "Zm9v",
        SoloApiVersion = api,
        SoloClientVersion = client,
    };

    [Fact]
    public async Task OffersACompatibleUpdate()
    {
        var update = await CheckAsync("1.0.0", Entry());

        Assert.NotNull(update);
        Assert.Equal("1.1.0", update.AvailableVersion);
        Assert.Equal("1.0.0", update.InstalledVersion);
        Assert.True(update.Compatible);
        Assert.Null(update.Incompatibility);
    }

    [Fact]
    public async Task ShowsAServerIncompatibilityWithoutOfferingIt()
    {
        var update = await CheckAsync("1.0.0", Entry(api: ">=99.0 <100.0"));

        Assert.NotNull(update);
        Assert.False(update.Compatible);
        Assert.Contains("Solo server in the range", update.Incompatibility);
    }

    /// <summary>
    /// The check that was missing. A client range this build does not satisfy
    /// leaves the extension loaded on the server with its pages skipped in the
    /// browser, which is not a state to hand someone from an Update button.
    /// </summary>
    [Fact]
    public async Task ShowsAClientIncompatibilityWithoutOfferingIt()
    {
        var update = await CheckAsync("1.0.0", Entry(client: ">=99.0 <100.0"));

        Assert.NotNull(update);
        Assert.False(update.Compatible);
        Assert.Contains("Solo client in the range", update.Incompatibility);
        Assert.Contains("pages would not load", update.Incompatibility);
    }

    [Fact]
    public async Task SaysNothingWhenTheInstalledVersionIsCurrent()
    {
        Assert.Null(await CheckAsync("1.1.0", Entry()));
        Assert.Null(await CheckAsync("1.2.0", Entry()));
    }

    /// <summary>
    /// An entry with nothing to download is a malformed feed, not an update: it
    /// would become a button that cannot work.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IgnoresAnEntryWithNoUrlOrNoDigest(bool dropUrl, bool dropDigest)
    {
        var entry = Entry();
        if (dropUrl) entry.Url = null;
        if (dropDigest) entry.Sha512 = null;

        Assert.Null(await CheckAsync("1.0.0", entry));
    }

    [Theory]
    [InlineData("1.21.0", "https://github.com/HunterAhlquist/Solo/releases/latest/download/release-manifest.json")]
    [InlineData("1.21.0+3c1f9e2", "https://github.com/HunterAhlquist/Solo/releases/latest/download/release-manifest.json")]
    [InlineData("1.21.0-mobile.3+3c1f9e2", "https://github.com/HunterAhlquist/Solo/releases/download/preview-v1.21.0-mobile.3/release-manifest.json")]
    [InlineData("1.18.0-design-docs.1", "https://github.com/HunterAhlquist/Solo/releases/download/preview-v1.18.0-design-docs.1/release-manifest.json")]
    public void AStableServerReadsTheLatestReleaseAndAPreviewItsOwn(string serverVersion, string expected) =>
        Assert.Equal(expected, new UpdateOptions().ManifestUrlFor(serverVersion));

    [Fact]
    public void AConfiguredManifestWinsForAPreviewToo() =>
        Assert.Equal(
            "https://mirror.invalid/release-manifest.json",
            new UpdateOptions { ManifestUrl = "https://mirror.invalid/release-manifest.json" }
                .ManifestUrlFor("1.21.0-mobile.3"));

    private const string HostFeed = "https://host.invalid/release-manifest.json";
    private const string BetaFeed = "https://feed.invalid/beta.json";

    /// <summary>
    /// Which feed is read once an administrator has picked one. The host
    /// manifest describes this package too, so an answer of 1.1.0 would mean
    /// the pick was ignored.
    /// </summary>
    private static async Task<ExtensionUpdateDto?> CheckWithPickAsync(string? picked)
    {
        var manifest = new ExtensionManifest
        {
            Id = "roadmap",
            Name = "Roadmap",
            Version = "1.0.0",
            Server = "server/Thing.dll",
            UpdateFeeds = [new ExtensionUpdateFeed { Id = "beta", Label = "Beta", Url = BetaFeed }],
        };
        var package = new ExtensionPackage { Manifest = manifest, Source = "solo-roadmap-1.0.0.zip" };
        var state = new ExtensionState([package], []);
        state.SelectUpdateFeed("roadmap", picked);

        var options = new UpdateOptions { ManifestUrl = HostFeed };
        var factory = new StubFactory(url => url switch
        {
            HostFeed => new ReleaseFeed { Schema = 1, Extensions = [Entry("1.1.0")] },
            BetaFeed => new ReleaseFeed { Schema = 1, Extensions = [Entry("1.2.0-beta.1")] },
            _ => null,
        });
        var feeds = new ReleaseFeedClient(
            factory, Options.Create(options), TimeProvider.System,
            NullLogger<ReleaseFeedClient>.Instance);
        var checker = new UpdateChecker(
            feeds, state, Options.Create(options), NullLogger<UpdateChecker>.Instance);

        var status = await checker.GetStatusAsync(force: false, CancellationToken.None);
        return status.Extensions.SingleOrDefault();
    }

    [Fact]
    public async Task WithNoPickTheHostManifestStillWins() =>
        Assert.Equal("1.1.0", (await CheckWithPickAsync(null))?.AvailableVersion);

    [Fact]
    public async Task APickedFeedIsReadInsteadOfTheHostManifest() =>
        Assert.Equal("1.2.0-beta.1", (await CheckWithPickAsync("beta"))?.AvailableVersion);

    /// <summary>A newer package can drop a feed; the stored pick must not strand it with no updates.</summary>
    [Fact]
    public async Task APickThePackageNoLongerDeclaresFallsBackToTheDefault() =>
        Assert.Equal("1.1.0", (await CheckWithPickAsync("nightly"))?.AvailableVersion);

    /// <summary>Serves a feed per URL, and 404 for any URL it has none for.</summary>
    private sealed class StubFactory(Func<string, ReleaseFeed?> serve) : IHttpClientFactory
    {
        public StubFactory(ReleaseFeed feed) : this(_ => feed) { }

        public HttpClient CreateClient(string name) =>
            new(new StubHandler(serve)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class StubHandler(Func<string, ReleaseFeed?> serve) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(serve(request.RequestUri!.ToString()) is { } feed
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(feed),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
