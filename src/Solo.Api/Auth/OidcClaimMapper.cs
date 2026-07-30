using System.Security.Claims;

namespace Solo.Api.Auth;

public sealed record MappedIdentity(
    string Issuer,
    string Subject,
    string? Email,
    string? DisplayName,
    string? AvatarUrl);

/// <summary>
/// Thin per-provider claim mapping. Providers are OIDC-compliant, but details
/// differ — e.g. Microsoft tokens often carry the email in
/// <c>preferred_username</c>. Expects raw (unmapped) JWT claim types.
/// </summary>
public static class OidcClaimMapper
{
    public const string Google = "google";
    public const string Microsoft = "microsoft";
    public const string Generic = "generic";

    public static MappedIdentity? Map(ClaimsPrincipal principal, string provider)
    {
        var issuer = FirstClaim(principal, "iss");
        var subject = FirstClaim(principal, "sub", ClaimTypes.NameIdentifier);
        if (issuer is null || subject is null)
        {
            return null;
        }

        // The email is not just a display field: an invite is activated by
        // matching it (UserProvisioningService), so an issuer that will mint a
        // token for an address its owner never proved would otherwise hand out
        // somebody else's project membership. Providers say so with
        // email_verified — when it is present and false, there is no email
        // here, and the identity provisions nothing.
        var email = EmailVerified(principal)
            ? FirstClaim(principal, "email", ClaimTypes.Email)
            : null;
        if (email is null && EmailVerified(principal) &&
            provider.Equals(Microsoft, StringComparison.OrdinalIgnoreCase))
        {
            // Entra ID commonly puts the sign-in email here; only trust it when
            // it looks like an address.
            var preferred = FirstClaim(principal, "preferred_username");
            if (preferred is not null && preferred.Contains('@'))
            {
                email = preferred;
            }
        }

        var name = FirstClaim(principal, "name", ClaimTypes.Name);
        if (name is null)
        {
            var given = FirstClaim(principal, "given_name", ClaimTypes.GivenName);
            var family = FirstClaim(principal, "family_name", ClaimTypes.Surname);
            name = (given, family) switch
            {
                (null, null) => null,
                _ => $"{given} {family}".Trim(),
            };
        }

        return new MappedIdentity(
            issuer,
            subject,
            email?.ToLowerInvariant(),
            name,
            FirstClaim(principal, "picture"));
    }

    /// <summary>
    /// False only when the token explicitly says the address is unverified.
    /// An absent claim is treated as verified: several compliant issuers omit
    /// it, and the session scheme's re-issued principal carries no such claim
    /// either — the address it replays was already checked at sign-in.
    /// </summary>
    private static bool EmailVerified(ClaimsPrincipal principal) =>
        FirstClaim(principal, "email_verified") is not { } value ||
        !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    private static string? FirstClaim(ClaimsPrincipal principal, params string[] types) =>
        types.Select(principal.FindFirstValue)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
