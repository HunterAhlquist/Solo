using Solo.Api.Updates;
using Xunit;

namespace Solo.Api.Tests;

/// <summary>
/// The update check is one comparison, and every way of getting it wrong is
/// silent: a missed update is a user who never hears about a fix, and a false
/// one is a permanent badge nothing can clear.
/// </summary>
public sealed class SemanticVersionTests
{
    [Theory]
    // The shape /api/config/version actually reports. Build metadata takes no
    // part in precedence, so the same release rebuilt is not an update.
    [InlineData("1.18.0", "1.16.6+c194865595352bea514f1295e4c39c8391fcc01c", true)]
    [InlineData("1.16.6", "1.16.6+c194865595352bea514f1295e4c39c8391fcc01c", false)]
    [InlineData("1.16.6+aaaa", "1.16.6+bbbb", false)]
    // Ordinary precedence, including the one a string comparison inverts.
    [InlineData("1.18.0", "1.9.0", true)]
    [InlineData("1.9.0", "1.18.0", false)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.16.7", "1.16.6", true)]
    [InlineData("1.16.6", "1.16.7", false)]
    // A prerelease is below the release it leads to, so a preview is correctly
    // told the stable release supersedes it — and a stable build is never
    // offered a prerelease.
    [InlineData("1.18.0", "1.18.0-rc.1", true)]
    [InlineData("1.18.0-rc.1", "1.18.0", false)]
    [InlineData("1.18.0-rc.2", "1.18.0-rc.1", true)]
    // Numeric prerelease identifiers compare as numbers.
    [InlineData("1.18.0-rc.10", "1.18.0-rc.2", true)]
    [InlineData("1.18.0-rc.2", "1.18.0-rc.10", false)]
    // A leading v is how the same number is written as a tag.
    [InlineData("v1.18.0", "1.16.6", true)]
    // Nothing to compare against is not an update.
    [InlineData("1.18.0", "", false)]
    [InlineData("", "1.16.6", false)]
    [InlineData("1.18.0", "not-a-version", false)]
    [InlineData("not-a-version", "1.16.6", false)]
    public void IsNewer(string candidate, string running, bool expected) =>
        Assert.Equal(expected, SemanticVersion.IsNewer(candidate, running));

    [Theory]
    [InlineData("1.18.0", 1, 18, 0, null)]
    [InlineData("1.18", 1, 18, 0, null)]
    [InlineData("1", 1, 0, 0, null)]
    [InlineData("1.18.0-rc.1", 1, 18, 0, "rc.1")]
    [InlineData("1.18.0-rc.1+abc", 1, 18, 0, "rc.1")]
    [InlineData("v1.18.0", 1, 18, 0, null)]
    public void Parses(string text, int major, int minor, int patch, string? prerelease)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(new SemanticVersion(major, minor, patch, prerelease), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.0")]
    [InlineData("1.18.0-")]
    [InlineData("latest")]
    public void RefusesNonVersions(string? text) =>
        Assert.False(SemanticVersion.TryParse(text, out _));

    /// <summary>
    /// A prerelease with fewer identifiers sorts below a longer one sharing its
    /// prefix, and a numeric identifier below an alphanumeric one. Spec rules
    /// that only bite once a release names something like <c>rc.1.hotfix</c>.
    /// </summary>
    [Fact]
    public void PrereleaseOrderFollowsTheSpec()
    {
        Assert.True(SemanticVersion.IsNewer("1.0.0-alpha.1", "1.0.0-alpha"));
        Assert.True(SemanticVersion.IsNewer("1.0.0-alpha.beta", "1.0.0-alpha.1"));
        Assert.True(SemanticVersion.IsNewer("1.0.0-beta", "1.0.0-alpha.beta"));
        Assert.True(SemanticVersion.IsNewer("1.0.0-rc.1", "1.0.0-beta.11"));
    }
}
