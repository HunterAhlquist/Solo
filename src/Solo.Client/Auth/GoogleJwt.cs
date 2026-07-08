using System.Security.Claims;
using System.Text.Json;

namespace Solo.Client.Auth;

/// <summary>
/// Reads the claims out of a Google Identity Services credential (an ID token JWT)
/// so the UI can show who is signed in.
/// </summary>
/// <remarks>
/// This does <em>not</em> verify the signature, and it must not pretend to. The API
/// is the only thing that trusts a token: it validates the signature against
/// Google's JWKS, the issuer, and <c>aud == ClientId</c>. Everything read here is
/// cosmetic — a display name, an avatar, and an expiry used to decide when to stop
/// sending a token that would be rejected anyway.
/// </remarks>
public static class GoogleJwt
{
    /// <summary>Claim types the server's OidcClaimMapper reads, kept as raw JWT names.</summary>
    private static readonly string[] Interesting =
        ["iss", "sub", "email", "name", "given_name", "family_name", "picture"];

    public static ClaimsPrincipal? ToPrincipal(string? token)
    {
        if (!TryReadPayload(token, out var payload))
        {
            return null;
        }

        var claims = new List<Claim>();
        foreach (var name in Interesting)
        {
            if (payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                claims.Add(new Claim(name, value.GetString()!));
            }
        }

        if (claims.Count == 0)
        {
            return null;
        }

        // NameClaimType "name" matches how the API is configured, so
        // context.User.Identity.Name is the display name in both places.
        var identity = new ClaimsIdentity(claims, "google", nameType: "name", roleType: "role");
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// True when the token is absent, unparseable, or within a minute of its
    /// <c>exp</c>. The leeway retires the token slightly <em>early</em>: a request
    /// that expires in flight comes back 401, and re-authenticating beats losing
    /// the user's edit.
    /// </summary>
    public static bool IsExpired(string? token)
    {
        if (!TryReadPayload(token, out var payload) ||
            !payload.TryGetProperty("exp", out var exp) ||
            !exp.TryGetInt64(out var seconds))
        {
            return true;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds) <= DateTimeOffset.UtcNow.AddSeconds(60);
    }

    private static bool TryReadPayload(string? token, out JsonElement payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            payload = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement.Clone();
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
