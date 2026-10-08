using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The production OIDC scheme, exactly as <c>Program.cs</c> configures it, fed
/// real signed ID tokens. Every other test swaps that scheme for the
/// header-driven <see cref="TestAuthHandler"/>, so this is what proves the rules
/// built on it hold for a genuine provider token — in particular that the
/// scheme stamps the identity that credential-minting endpoints accept.
/// </summary>
/// <remarks>
/// Only the provider's discovery document is replaced: the signing key and
/// issuer are supplied directly, so no network is needed.
/// </remarks>
[Collection("postgres")]
public sealed class ProviderTokenTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Issuer = "https://issuer.example.com";
    private const string ClientId = "solo-test-client";

    private static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(32));

    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        var connectionString = await postgres.CreateDatabaseAsync("provider_token");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Auth:Authority", Issuer);
            builder.UseSetting("Auth:ClientId", ClientId);
            builder.UseSetting("Auth:Provider", "generic");
            builder.UseSetting("Setup:Username", SoloApiFactory.SetupUsername);
            builder.UseSetting("Setup:Password", SoloApiFactory.SetupPassword);
            builder.UseSetting("RateLimiting:Api:Burst", "1000000");
            builder.UseSetting("RateLimiting:Auth:PermitLimit", "1000000");
            builder.ConfigureTestServices(services =>
                // Configure, not PostConfigure: JwtBearer's own post-configure
                // turns a supplied Configuration into a static manager, instead
                // of fetching the authority's discovery document.
                services.Configure<JwtBearerOptions>(Auth.AuthConstants.BearerScheme, options =>
                {
                    options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    options.Configuration.SigningKeys.Add(SigningKey);
                }));
        });
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private static string IdToken(string subject, string email) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["email"] = email,
                ["email_verified"] = true,
                ["name"] = subject,
            },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });

    private HttpClient WithBearer(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [SkippableFact]
    public async Task A_provider_token_mints_a_session_and_the_session_cannot_mint_another()
    {
        postgres.SkipUnlessDockerAvailable();

        using var admin = WithBearer(IdToken("admin-sub", "admin@example.com"));
        var setup = await admin.PostAsJsonAsync("/api/setup/complete", new
        {
            setupUsername = SoloApiFactory.SetupUsername,
            setupPassword = SoloApiFactory.SetupPassword,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        // A genuine provider sign-in is what the mint endpoint accepts...
        var minted = await admin.PostAsJsonAsync("/api/auth/sessions", new { description = "desktop" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        var session = (await minted.Content.ReadFromJsonAsync<SessionDto>(TestClients.Json))!;

        // ...and the session it bought works, but buys nothing further.
        using var viaSession = WithBearer(session.Token);
        Assert.Equal(HttpStatusCode.OK, (await viaSession.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viaSession.PostAsJsonAsync("/api/auth/sessions", new { }, TestClients.Json)).StatusCode);
    }

    [SkippableFact]
    public async Task A_valid_token_for_someone_nobody_invited_reaches_nothing()
    {
        postgres.SkipUnlessDockerAvailable();

        using var admin = WithBearer(IdToken("admin-sub", "admin@example.com"));
        await admin.PostAsJsonAsync("/api/setup/complete", new
        {
            setupUsername = SoloApiFactory.SetupUsername,
            setupPassword = SoloApiFactory.SetupPassword,
        }, TestClients.Json);

        using var drifter = WithBearer(IdToken("drifter-sub", "drifter@example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, (await drifter.GetAsync("/api/extensions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await drifter.PostAsJsonAsync("/api/auth/sessions", new { }, TestClients.Json)).StatusCode);

        // A token that isn't for this instance gets nowhere at all.
        using var forged = WithBearer("eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/extensions")).StatusCode);
    }
}
