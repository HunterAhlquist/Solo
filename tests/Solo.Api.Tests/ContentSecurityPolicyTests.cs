using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
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
        static string WithoutProvider(string policy) =>
            string.Join("; ", policy.Split("; ").Where(d =>
                !d.StartsWith("connect-src ", StringComparison.Ordinal) &&
                !d.StartsWith("frame-src ", StringComparison.Ordinal)));

        Assert.Equal(
            WithoutProvider(SecurityHeaders.ContentSecurityPolicyFor("google", "https://accounts.google.com")),
            WithoutProvider(SecurityHeaders.ContentSecurityPolicyFor("microsoft",
                "https://login.microsoftonline.com/tenant/v2.0")));
    }

    [Fact]
    public void The_callback_page_may_be_framed_by_its_own_origin_alone()
    {
        var policy = SecurityHeaders.AllowFramingBySelf(
            SecurityHeaders.ContentSecurityPolicyFor("generic", "https://id.example.com"));

        Assert.Single(policy.Split("; "), d => d == "frame-ancestors 'self'");
        Assert.DoesNotContain("frame-ancestors 'none'", policy);
    }

    [Theory]
    [InlineData("google", "https://accounts.google.com", false)]
    [InlineData("microsoft", "https://login.microsoftonline.com/t/v2.0", true)]
    [InlineData("generic", "https://id.example.com", true)]
    [InlineData("generic", "", false)]
    public void Only_a_code_flow_provider_renews_in_a_frame(string provider, string authority, bool renews) =>
        Assert.Equal(renews, new ContentSecurityPolicies(provider, authority, discover: null,
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContentSecurityPolicies>.Instance).RenewsInAFrame);

    [Fact]
    public void Google_frames_only_Google()
    {
        var policy = SecurityHeaders.ContentSecurityPolicyFor("google", "https://accounts.google.com");

        Assert.Single(policy.Split("; "), d => d == "frame-src https://accounts.google.com");
    }
}

/// <summary>
/// The provider's endpoints come from its discovery document, because they
/// needn't share the issuer's origin: Entra ID's userinfo endpoint is on
/// graph.microsoft.com, and the web client's OIDC library calls it straight
/// after the token exchange.
/// </summary>
public sealed class ContentSecurityPoliciesTests
{
    private const string Authority = "https://login.microsoftonline.com/0f1e2d3c-0000-0000-0000-000000000000/v2.0";

    private static readonly OpenIdConnectConfiguration Entra = new()
    {
        AuthorizationEndpoint = "https://login.microsoftonline.com/0f1e2d3c-0000-0000-0000-000000000000/oauth2/v2.0/authorize",
        TokenEndpoint = "https://login.microsoftonline.com/0f1e2d3c-0000-0000-0000-000000000000/oauth2/v2.0/token",
        UserInfoEndpoint = "https://graph.microsoft.com/oidc/userinfo",
        JwksUri = "https://login.microsoftonline.com/0f1e2d3c-0000-0000-0000-000000000000/discovery/v2.0/keys",
    };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string ConnectSrc(string policy) =>
        Assert.Single(policy.Split("; "), d => d.StartsWith("connect-src ", StringComparison.Ordinal));

    private static string FrameSrc(string policy) =>
        Assert.Single(policy.Split("; "), d => d.StartsWith("frame-src ", StringComparison.Ordinal));

    private static ContentSecurityPolicies For(
        string provider, Func<CancellationToken, Task<OpenIdConnectConfiguration>>? discover, TimeProvider? time = null) =>
        new(provider, Authority, discover, time ?? new Clock(), NullLogger<ContentSecurityPolicies>.Instance);

    [Fact]
    public async Task Entra_gets_its_userinfo_origin_as_well_as_its_own()
    {
        var policies = For("microsoft", _ => Task.FromResult(Entra));

        var policy = await policies.GetAsync();
        Assert.Equal(
            "connect-src 'self' https://accounts.google.com https://login.microsoftonline.com https://graph.microsoft.com",
            ConnectSrc(policy));
        // Silent renewal frames the authorization endpoint; userinfo isn't framed.
        Assert.Equal("frame-src https://accounts.google.com 'self' https://login.microsoftonline.com", FrameSrc(policy));
    }

    [Fact]
    public async Task A_session_check_page_on_another_origin_may_be_framed()
    {
        var policies = For("generic", _ => Task.FromResult(new OpenIdConnectConfiguration
        {
            AuthorizationEndpoint = "https://login.microsoftonline.com/t/authorize",
            CheckSessionIframe = "https://session.example.com/check",
        }));

        Assert.Equal("frame-src https://accounts.google.com 'self' https://login.microsoftonline.com https://session.example.com",
            FrameSrc(await policies.GetAsync()));
    }

    [Fact]
    public async Task Google_never_reads_a_discovery_document()
    {
        var policies = For("google", _ => throw new InvalidOperationException("must not be called"));

        Assert.Equal("connect-src 'self' https://accounts.google.com", ConnectSrc(await policies.GetAsync()));
    }

    [Fact]
    public async Task Endpoints_are_reduced_to_safe_origins()
    {
        var policies = For("generic", _ => Task.FromResult(new OpenIdConnectConfiguration
        {
            TokenEndpoint = "https://login.microsoftonline.com/t/token; script-src *",
            UserInfoEndpoint = "http://userinfo.example.com/me",
            JwksUri = "javascript:alert(1)",
        }));

        var policy = await policies.GetAsync();
        Assert.Equal("connect-src 'self' https://accounts.google.com https://login.microsoftonline.com", ConnectSrc(policy));
        Assert.DoesNotContain("script-src *", policy);
    }

    [Fact]
    public async Task An_unreadable_document_serves_the_issuer_alone_and_is_retried_later()
    {
        var clock = new Clock();
        var calls = 0;
        var fail = true;
        var policies = For("microsoft", _ =>
        {
            calls++;
            return fail
                ? Task.FromException<OpenIdConnectConfiguration>(new HttpRequestException("down"))
                : Task.FromResult(Entra);
        }, clock);

        Assert.Equal("connect-src 'self' https://accounts.google.com https://login.microsoftonline.com",
            ConnectSrc(await policies.GetAsync()));
        Assert.Equal(1, calls);

        // Not asked again on every response while it's down.
        fail = false;
        await policies.GetAsync();
        Assert.Equal(1, calls);

        clock.Now += ContentSecurityPolicies.RetryAfter;
        await policies.GetAsync();
        Assert.Equal(2, calls);
        Assert.Contains("https://graph.microsoft.com", ConnectSrc(await policies.GetAsync()));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Without_a_bearer_scheme_the_policy_is_the_issuers()
    {
        var policies = For("generic", discover: null);

        Assert.Equal("connect-src 'self' https://accounts.google.com https://login.microsoftonline.com",
            ConnectSrc(await policies.GetAsync()));
    }
}
