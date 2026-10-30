using System.Text.Json;
using Solo.Api.Updates;
using Xunit;

namespace Solo.Api.Tests;

/// <summary>
/// The release manifest is written by <c>build/release-manifest.mjs</c> in
/// JavaScript and read here in C#, with nothing but convention holding the two
/// shapes together — so this parses a real one, taken verbatim off a published
/// release, rather than one written to match the reader.
/// </summary>
/// <remarks>
/// Drift here is silent in the worst way: a renamed field deserialises to null,
/// the comparison finds nothing newer, and every instance quietly reports that
/// it is up to date forever. Refresh the fixture by downloading the asset from
/// a release.
/// </remarks>
public sealed class ReleaseFeedTests
{
    private static ReleaseFeed Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "release-manifest.json");
        var feed = JsonSerializer.Deserialize<ReleaseFeed>(
            File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Assert.IsType<ReleaseFeed>(feed);
    }

    [Fact]
    public void ReadsTheReleaseAndServerEntries()
    {
        var feed = Load();

        Assert.Equal(1, feed.Schema);
        Assert.Equal("preview-v1.18.0-rc.1", feed.Release!.Tag);
        Assert.Equal("1.18.0-rc.1", feed.Release.Version);
        Assert.True(feed.Release.Prerelease);
        Assert.NotNull(feed.Release.Published);

        Assert.Equal("1.18.0-rc.1", feed.Server!.Version);
        Assert.Equal("preview-v1.18.0-rc.1", feed.Server.Release);
        Assert.Equal("ghcr.io/hunterahlquist/solo:1.18.0-rc.1", feed.Server.Image);
        // The contract this release shipped, not the one in this checkout: the
        // fixture is history, and asserting the current number here would mean
        // re-capturing it on every contract bump for no benefit.
        Assert.Equal("1.11.1", feed.Server.Contract);
    }

    /// <summary>
    /// Every field the extension updater needs, on a real entry. Missing a url
    /// or a sha512 makes an entry unusable, so these are what the checker
    /// refuses an update for.
    /// </summary>
    [Fact]
    public void ReadsEverythingAnExtensionUpdateNeeds()
    {
        var feed = Load();

        var roadmap = Assert.Single(feed.Extensions, e => e.Id == "roadmap");
        Assert.Equal("1.8.1", roadmap.Version);
        Assert.Equal("solo-roadmap-1.8.1.zip", roadmap.Asset);
        Assert.StartsWith("https://github.com/", roadmap.Url);
        Assert.EndsWith("solo-roadmap-1.8.1.zip", roadmap.Url);
        Assert.True(roadmap.Size > 0);
        Assert.False(string.IsNullOrWhiteSpace(roadmap.Sha512));

        // camelCase in the file, PascalCase on the property: these two are the
        // ones that need an explicit JsonPropertyName, so a rename shows up here.
        Assert.Equal(">=1.4 <2.0", roadmap.SoloApiVersion);
        Assert.Equal(">=1.6 <2.0", roadmap.SoloClientVersion);

        Assert.Equal(
            new[] { "design-docs", "mcp", "roadmap" },
            feed.Extensions.Select(e => e.Id).Order().ToArray());
    }

    /// <summary>
    /// The end-to-end answer: an older instance reading this feed is told to
    /// update, and one already on it is not.
    /// </summary>
    [Fact]
    public void DrivesTheServerComparison()
    {
        var available = Load().Server!.Version;

        Assert.True(SemanticVersion.IsNewer(available, "1.16.6+c194865595352bea514f1295e4c39c8391fcc01c"));
        Assert.False(SemanticVersion.IsNewer(available, "1.18.0-rc.1"));
        Assert.False(SemanticVersion.IsNewer(available, "1.18.0"));
    }
}
