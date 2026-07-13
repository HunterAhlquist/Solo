using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// Rate limiting: the strict per-IP window on credential endpoints, the
/// per-caller bucket over the rest of the API, and the exemption that keeps
/// the served WASM client outside the limiter. Each test tightens the limits
/// it exercises via config; SoloApiFactory leaves them effectively unlimited
/// for everyone else.
/// </summary>
[Collection("postgres")]
public sealed class RateLimitingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private SoloApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        var connectionString = await postgres.CreateDatabaseAsync("ratelimit");
        _factory = new SoloApiFactory(connectionString);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private WebApplicationFactory<Program> WithLimits(params (string Key, string Value)[] settings) =>
        _factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    [SkippableFact]
    public async Task AuthPolicy_ThrottlesCredentialEndpointWithRetryAfter()
    {
        postgres.SkipUnlessDockerAvailable();

        using var factory = WithLimits(("RateLimiting:Auth:PermitLimit", "2"));
        using var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            var allowed = await client.PostAsJsonAsync(
                "/api/auth/desktop/start", new { description = "test" }, TestClients.Json);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var limited = await client.PostAsJsonAsync(
            "/api/auth/desktop/start", new { description = "test" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [SkippableFact]
    public async Task GlobalLimiter_MetersApiButNotServedClient()
    {
        postgres.SkipUnlessDockerAvailable();

        // No replenishment within the test: 2-token bucket, hour-long period.
        using var factory = WithLimits(
            ("RateLimiting:Api:Burst", "2"),
            ("RateLimiting:Api:TokensPerPeriod", "1"),
            ("RateLimiting:Api:PeriodSeconds", "3600"));
        using var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            var allowed = await client.GetAsync("/api/setup/status");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var limited = await client.GetAsync("/api/setup/status");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // Everything outside /api — health probes, the served WASM client —
        // stays exempt even with the API bucket drained.
        var health = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var spa = await client.GetAsync("/");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, spa.StatusCode);
    }

    [SkippableFact]
    public async Task GlobalLimiter_PartitionsPerUser()
    {
        postgres.SkipUnlessDockerAvailable();

        using var factory = WithLimits(
            ("RateLimiting:Api:Burst", "2"),
            ("RateLimiting:Api:TokensPerPeriod", "1"),
            ("RateLimiting:Api:PeriodSeconds", "3600"));

        using var alice = factory.CreateClient();
        alice.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, "alice");
        alice.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, "alice@example.com");
        using var bob = factory.CreateClient();
        bob.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, "bob");
        bob.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, "bob@example.com");

        for (var i = 0; i < 2; i++)
        {
            var allowed = await alice.GetAsync("/api/projects");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var aliceLimited = await alice.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.TooManyRequests, aliceLimited.StatusCode);

        // Alice exhausting her bucket must not throttle Bob.
        var bobAllowed = await bob.GetAsync("/api/projects");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, bobAllowed.StatusCode);
    }
}
