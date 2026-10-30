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

    /// <summary>Serves one feed for any URL; the point here is the comparison, not resolution.</summary>
    private sealed class StubFactory(ReleaseFeed feed) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler(feed)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class StubHandler(ReleaseFeed feed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(feed),
            });
    }
}
