using System.Text;
using System.Text.Json;
using Solo.Client.Auth;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// Google Identity Services returns the ID token directly, so the client reads its
/// claims for display. Nothing here is a security boundary — the API re-validates
/// signature, issuer and audience on every request — but a wrong expiry check would
/// either sign users out early or keep sending tokens the server rejects.
/// </summary>
public class GoogleJwtTests
{
    /// <summary>Builds an unsigned JWT with the shape GIS credentials have.</summary>
    private static string Credential(
        DateTimeOffset? expires = null,
        string? name = "Hunter Ahlquist",
        string? email = "hunter@example.com",
        bool malformedPayload = false)
    {
        var payload = new Dictionary<string, object>
        {
            ["iss"] = "https://accounts.google.com",
            ["sub"] = "1234567890",
            ["exp"] = (expires ?? DateTimeOffset.UtcNow.AddHours(1)).ToUnixTimeSeconds(),
            ["picture"] = "https://example.com/a.png",
        };

        if (name is not null)
        {
            payload["name"] = name;
        }

        if (email is not null)
        {
            payload["email"] = email;
        }

        var body = malformedPayload
            ? "not-json"
            : JsonSerializer.Serialize(payload);

        return $"{Base64Url("{\"alg\":\"RS256\"}")}.{Base64Url(body)}.signature-not-checked-here";
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void A_credential_yields_the_claims_the_server_maps()
    {
        var principal = GoogleJwt.ToPrincipal(Credential());

        Assert.NotNull(principal);
        Assert.Equal("Hunter Ahlquist", principal.Identity!.Name);
        Assert.Equal("hunter@example.com", principal.FindFirst("email")!.Value);
        Assert.Equal("https://accounts.google.com", principal.FindFirst("iss")!.Value);
        Assert.Equal("1234567890", principal.FindFirst("sub")!.Value);
    }

    [Fact]
    public void A_live_credential_is_not_expired()
    {
        Assert.False(GoogleJwt.IsExpired(Credential(DateTimeOffset.UtcNow.AddMinutes(30))));
    }

    [Fact]
    public void A_credential_past_its_exp_is_expired()
    {
        Assert.True(GoogleJwt.IsExpired(Credential(DateTimeOffset.UtcNow.AddMinutes(-5))));
    }

    [Fact]
    public void A_credential_expiring_within_the_leeway_is_retired_early()
    {
        // 30s of life left is inside the one-minute skew: stop sending it, rather
        // than let it expire mid-request and 401 in the middle of a save.
        Assert.True(GoogleJwt.IsExpired(Credential(DateTimeOffset.UtcNow.AddSeconds(30))));

        // Comfortably outside the skew: still good.
        Assert.False(GoogleJwt.IsExpired(Credential(DateTimeOffset.UtcNow.AddMinutes(5))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d")]
    public void Rubbish_is_expired_and_yields_no_principal(string? token)
    {
        Assert.True(GoogleJwt.IsExpired(token));
        Assert.Null(GoogleJwt.ToPrincipal(token));
    }

    [Fact]
    public void A_malformed_payload_is_rejected_rather_than_throwing()
    {
        var token = Credential(malformedPayload: true);

        Assert.True(GoogleJwt.IsExpired(token));
        Assert.Null(GoogleJwt.ToPrincipal(token));
    }

    [Fact]
    public void A_credential_with_no_recognised_claims_yields_no_principal()
    {
        var token = $"{Base64Url("{}")}.{Base64Url("{\"nothing\":1}")}.sig";

        Assert.Null(GoogleJwt.ToPrincipal(token));
    }
}

public class AppConfigProviderTests
{
    private static AppConfig For(string? provider, string authority = "https://issuer.test", string clientId = "cid") =>
        new() { Auth = new AuthConfigDto(authority, clientId, provider ?? string.Empty, true) };

    [Theory]
    [InlineData("google")]
    [InlineData("Google")]
    [InlineData("GOOGLE")]
    public void Google_uses_Identity_Services_not_the_redirect_flow(string provider)
    {
        var config = For(provider);

        Assert.True(config.IsGoogle);
        Assert.False(config.UsesOidcRedirect);
    }

    [Theory]
    [InlineData("microsoft")]
    [InlineData("generic")]
    public void Every_other_provider_uses_code_plus_PKCE(string provider)
    {
        var config = For(provider);

        Assert.False(config.IsGoogle);
        Assert.True(config.UsesOidcRedirect);
    }

    [Fact]
    public void An_unconfigured_instance_uses_neither_flow()
    {
        var config = For("google", authority: string.Empty, clientId: string.Empty);

        Assert.False(config.ProviderConfigured);
        Assert.False(config.IsGoogle);
        Assert.False(config.UsesOidcRedirect);
    }
}
