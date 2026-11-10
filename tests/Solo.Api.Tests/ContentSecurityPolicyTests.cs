using Xunit;

namespace Solo.Api.Tests;

/// <summary>
/// A code + PKCE sign-in fetches the provider's discovery document and token
/// endpoint from the browser, so connect-src has to allow the provider's
/// origin or every Microsoft and generic sign-in ends in a network error. The
/// value lands in a response header, so nothing but a parsed origin may.
/// </summary>
public sealed class ContentSecurityPolicyTests
{
    private static string ConnectSrc(string? provider, string? authority)
    {
        var policy = SecurityHeaders.ContentSecurityPolicyFor(provider, authority);
        return Assert.Single(policy.Split("; "), d => d.StartsWith("connect-src ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("google", "https://accounts.google.com")]
    [InlineData("Google", "https://accounts.google.com")]
    public void Google_keeps_the_policy_it_had(string provider, string authority) =>
        Assert.Equal("connect-src 'self' https://accounts.google.com", ConnectSrc(provider, authority));

    [Theory]
    [InlineData("microsoft", "https://login.microsoftonline.com/0f1e2d3c-0000-0000-0000-000000000000/v2.0",
        "https://login.microsoftonline.com")]
    [InlineData("generic", "https://id.example.com/realms/solo", "https://id.example.com")]
    [InlineData("generic", "https://id.example.com:8443/", "https://id.example.com:8443")]
    [InlineData("generic", "http://localhost:8080/default", "http://localhost:8080")]
    public void Another_provider_adds_its_origin_and_nothing_else(string provider, string authority, string origin) =>
        Assert.Equal($"connect-src 'self' https://accounts.google.com {origin}", ConnectSrc(provider, authority));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/issuer")]
    // Plain http anywhere but loopback would let a script talk to the
    // provider's host in the clear.
    [InlineData("http://id.example.com")]
    [InlineData("ftp://id.example.com")]
    [InlineData("https://user:secret@id.example.com")]
    public void An_authority_that_is_not_a_plain_https_origin_adds_nothing(string? authority) =>
        Assert.Equal("connect-src 'self' https://accounts.google.com", ConnectSrc("generic", authority));

    [Fact]
    public void An_authority_cannot_write_its_own_directives()
    {
        var policy = SecurityHeaders.ContentSecurityPolicyFor(
            "generic", "https://id.example.com/x; script-src *; connect-src *");

        Assert.Equal("connect-src 'self' https://accounts.google.com https://id.example.com",
            ConnectSrc("generic", "https://id.example.com/x; script-src *; connect-src *"));
        Assert.DoesNotContain("script-src *", policy);
        Assert.Single(policy.Split("; "), d => d.StartsWith("connect-src ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rest_of_the_policy_is_the_same_for_every_provider()
    {
        static string WithoutConnect(string policy) =>
            string.Join("; ", policy.Split("; ").Where(d => !d.StartsWith("connect-src ", StringComparison.Ordinal)));

        Assert.Equal(
            WithoutConnect(SecurityHeaders.ContentSecurityPolicyFor("google", "https://accounts.google.com")),
            WithoutConnect(SecurityHeaders.ContentSecurityPolicyFor("microsoft",
                "https://login.microsoftonline.com/tenant/v2.0")));
    }
}
