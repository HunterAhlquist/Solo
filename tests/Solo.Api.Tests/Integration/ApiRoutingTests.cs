using System.Net;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The API namespace and the SPA fallback answer unknown routes differently:
/// a missing <c>/api/*</c> route is a JSON 404 an API client can read, while a
/// non-API path still boots the client so deep links work.
/// </summary>
[Collection("postgres")]
public sealed class ApiRoutingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private SoloApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("api_routing"));
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task UnknownApiRoute_Returns404Json_NotTheSpaShell()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _factory.CreateClient().GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<!doctype html>",
            await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task UnknownClientRoute_StillBootsTheSpa()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _factory.CreateClient().GetAsync("/some/deep/client-route");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
