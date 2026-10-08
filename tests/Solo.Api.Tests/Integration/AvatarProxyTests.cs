using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solo.Api.Controllers;
using Solo.Data;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The avatar endpoint serves bytes another host chose, from the app's own
/// origin. The picture host is stubbed at the avatar client's primary handler,
/// so everything above the socket (type checks, headers, caching) runs as
/// deployed.
/// </summary>
[Collection("postgres")]
public sealed class AvatarProxyTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly UpstreamPicture _upstream = new();
    private WebApplicationFactory<Program> _factory = null!;
    private Guid _userId;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        var upstream = _upstream;
        _factory = new SoloApiFactory(await postgres.CreateDatabaseAsync("avatars"))
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddHttpClient(AvatarConstants.HttpClient)
                    .ConfigurePrimaryHttpMessageHandler(() => new UpstreamHandler(upstream))));
        using var admin = _factory.As("admin", "admin@example.com", "Admin");
        _userId = (await TestClients.CompleteSetupAsync(admin)).Id;

        // As the provider's picture claim would have left it.
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
        await db.Users.Where(u => u.Id == _userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.AvatarUrl, "https://pictures.example.com/admin"));
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableTheory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    [InlineData("image/gif")]
    public async Task A_raster_picture_is_served_under_the_isolation_policy(string contentType)
    {
        postgres.SkipUnlessDockerAvailable();
        _upstream.ContentType = contentType;

        using var anon = _factory.CreateClient();
        var response = await anon.GetAsync($"/api/users/{_userId}/avatar");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("sandbox", csp);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    /// <summary>
    /// An SVG is a document. Served from this origin, it would run its script
    /// for anyone who opened the avatar URL directly, so it is not served at
    /// all: the client falls back to initials, as for a user with no picture.
    /// </summary>
    [SkippableTheory]
    [InlineData("image/svg+xml")]
    [InlineData("image/x-icon")]
    [InlineData("text/html")]
    public async Task Any_other_type_is_refused(string contentType)
    {
        postgres.SkipUnlessDockerAvailable();
        _upstream.ContentType = contentType;

        using var anon = _factory.CreateClient();
        var response = await anon.GetAsync($"/api/users/{_userId}/avatar");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class UpstreamPicture
    {
        public string ContentType { get; set; } = "image/png";
    }

    /// <summary>
    /// A new handler per client, sharing the picture: the factory disposes
    /// handlers as they expire, so a single shared instance would not survive.
    /// </summary>
    private sealed class UpstreamHandler(UpstreamPicture picture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = picture.ContentType == "image/svg+xml"
                ? "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray()
                : [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue(picture.ContentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
