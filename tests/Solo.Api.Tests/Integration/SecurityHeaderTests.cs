using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The instance serves the app and the API from one origin, and that origin
/// holds the session token in web storage. These pin the headers that keep a
/// script injected anywhere in it from being useful, and keep the app out of
/// somebody else's frame.
/// </summary>
[Collection("postgres")]
public sealed class SecurityHeaderTests(PostgresFixture postgres) : IAsyncLifetime
{
    private SoloApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("security_headers"));
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableTheory]
    [InlineData("/api/config/auth")]
    [InlineData("/healthz")]
    public async Task EveryResponse_CarriesTheAppPolicy(string path)
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _factory.CreateClient().GetAsync(path);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

        // Nothing here is meant to be framed, and an injected <base> would
        // repoint every relative asset the client loads.
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("base-uri 'self'", csp);
        Assert.Contains("img-src 'self' data: https: blob:", csp);
        Assert.Contains("object-src 'none'", csp);
        // Blazor WebAssembly needs this and nothing broader — in particular
        // no bare 'unsafe-eval'.
        Assert.Contains("'wasm-unsafe-eval'", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-eval'", csp);

        // Same-origin connections only. No bare ws:/wss: scheme source that
        // would let an injected script open a socket to any host.
        Assert.Contains("connect-src 'self' https://accounts.google.com", csp);
        Assert.DoesNotContain("ws:", csp);
        Assert.DoesNotContain("wss:", csp);

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("strict-origin-when-cross-origin",
            Assert.Single(response.Headers.GetValues("Referrer-Policy")));

        // HSTS must ride out even though the app serves plain HTTP behind the
        // TLS-terminating proxy (so UseHsts() would suppress it).
        Assert.Equal("max-age=63072000; includeSubDomains",
            Assert.Single(response.Headers.GetValues("Strict-Transport-Security")));
        // Powerful features the app never uses are denied.
        Assert.Contains("camera=()",
            Assert.Single(response.Headers.GetValues("Permissions-Policy")));
        // Private app: every response tells crawlers not to index it.
        Assert.Equal("noindex, nofollow",
            Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
    }

    /// <summary>
    /// The web client's OIDC library renews the sign-in in a hidden iframe
    /// that comes back to the callback page, so on an instance signing in with
    /// the code flow that one page may be framed — by the app's own origin
    /// only. Every other page, and that page anywhere else, stays unframeable.
    /// </summary>
    [SkippableFact]
    public async Task TheOidcCallback_MayBeFramedBySelf_OnACodeFlowInstance()
    {
        postgres.SkipUnlessDockerAvailable();

        // A provider authority here would register a second "Bearer" scheme
        // beside the test one, so the policy service is configured directly.
        using var codeFlow = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton(new ContentSecurityPolicies(
                "generic", "https://id.example.com/realms/solo", discover: null,
                TimeProvider.System, NullLogger<ContentSecurityPolicies>.Instance))));
        var client = codeFlow.CreateClient();

        var callback = await client.GetAsync("/authentication/login-callback?code=x&state=y");
        var callbackCsp = Assert.Single(callback.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'self'", callbackCsp);
        Assert.DoesNotContain("frame-ancestors 'none'", callbackCsp);
        Assert.Contains("connect-src 'self' https://accounts.google.com https://id.example.com", callbackCsp);
        // The page embedding the renewal iframe must allow it back on our origin.
        Assert.Contains("frame-src https://accounts.google.com 'self' https://id.example.com", callbackCsp);
        Assert.Equal("SAMEORIGIN", Assert.Single(callback.Headers.GetValues("X-Frame-Options")));

        foreach (var path in new[] { "/signin", "/api/config/auth", "/authentication/login" })
        {
            var other = await client.GetAsync(path);
            Assert.Contains("frame-ancestors 'none'", Assert.Single(other.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("DENY", Assert.Single(other.Headers.GetValues("X-Frame-Options")));
        }
    }

    [SkippableFact]
    public async Task TheOidcCallback_StaysUnframeable_WithoutACodeFlowProvider()
    {
        postgres.SkipUnlessDockerAvailable();

        // The default test instance has no authority, so nothing renews in a frame.
        var callback = await _factory.CreateClient().GetAsync("/authentication/login-callback");

        Assert.Contains("frame-ancestors 'none'", Assert.Single(callback.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("DENY", Assert.Single(callback.Headers.GetValues("X-Frame-Options")));
    }
}
