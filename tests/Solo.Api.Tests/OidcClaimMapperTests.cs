using System.Security.Claims;
using Solo.Api.Auth;
using Xunit;

namespace Solo.Api.Tests;

public class OidcClaimMapperTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(
            claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "Test"));

    [Fact]
    public void Google_MapsStandardClaims()
    {
        var principal = Principal(
            ("iss", "https://accounts.google.com"),
            ("sub", "10987654321"),
            ("email", "Hunter@Example.com"),
            ("name", "Hunter A"),
            ("picture", "https://lh3.example/photo.jpg"));

        var mapped = OidcClaimMapper.Map(principal, OidcClaimMapper.Google);

        Assert.NotNull(mapped);
        Assert.Equal("https://accounts.google.com", mapped.Issuer);
        Assert.Equal("10987654321", mapped.Subject);
        Assert.Equal("hunter@example.com", mapped.Email);
        Assert.Equal("Hunter A", mapped.DisplayName);
        Assert.Equal("https://lh3.example/photo.jpg", mapped.AvatarUrl);
    }

    [Fact]
    public void Microsoft_FallsBackToPreferredUsername_ForEmail()
    {
        var principal = Principal(
            ("iss", "https://login.microsoftonline.com/tenant/v2.0"),
            ("sub", "abc123"),
            ("preferred_username", "Hunter@Contoso.com"),
            ("name", "Hunter A"));

        var mapped = OidcClaimMapper.Map(principal, OidcClaimMapper.Microsoft);

        Assert.NotNull(mapped);
        Assert.Equal("hunter@contoso.com", mapped.Email);
    }

    [Fact]
    public void Microsoft_IgnoresNonEmailPreferredUsername()
    {
        var principal = Principal(
            ("iss", "https://login.microsoftonline.com/tenant/v2.0"),
            ("sub", "abc123"),
            ("preferred_username", "hunter.legacy-upn"));

        var mapped = OidcClaimMapper.Map(principal, OidcClaimMapper.Microsoft);

        Assert.NotNull(mapped);
        Assert.Null(mapped.Email);
    }

    [Fact]
    public void Generic_DoesNotUsePreferredUsername()
    {
        var principal = Principal(
            ("iss", "https://sso.example.com"),
            ("sub", "u1"),
            ("preferred_username", "hunter@example.com"));

        var mapped = OidcClaimMapper.Map(principal, OidcClaimMapper.Generic);

        Assert.NotNull(mapped);
        Assert.Null(mapped.Email);
    }

    [Fact]
    public void NameFallsBackToGivenAndFamilyName()
    {
        var principal = Principal(
            ("iss", "https://accounts.google.com"),
            ("sub", "1"),
            ("given_name", "Hunter"),
            ("family_name", "Ahlquist"));

        var mapped = OidcClaimMapper.Map(principal, OidcClaimMapper.Google);

        Assert.Equal("Hunter Ahlquist", mapped!.DisplayName);
    }

    [Fact]
    public void MissingIssuerOrSubject_YieldsNull()
    {
        Assert.Null(OidcClaimMapper.Map(Principal(("sub", "1")), OidcClaimMapper.Google));
        Assert.Null(OidcClaimMapper.Map(Principal(("iss", "x")), OidcClaimMapper.Google));
    }
}
