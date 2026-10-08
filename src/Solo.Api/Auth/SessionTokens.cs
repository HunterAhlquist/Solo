using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Solo.Api.Auth;

/// <summary>
/// The opaque bearer tokens behind <see cref="Solo.Data.Entities.AuthSession"/>.
/// The <c>solo_</c> prefix is what routes a request to the session scheme
/// instead of OIDC JWT validation; the database stores only the SHA-256.
/// </summary>
public static class SessionTokens
{
    public const string Prefix = "solo_";

    public static string Generate() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool IsSessionToken(string? token) =>
        token?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// The bearer credential of a request, from the Authorization header only.
    /// Never the query string, even on the hub: a URL ends up in access logs,
    /// and a session lasts 30 days. The hub takes a <see cref="HubTickets"/>
    /// ticket there instead.
    /// </summary>
    public static string? FromRequest(HttpRequest request)
    {
        string header = request.Headers.Authorization.ToString();
        return header.StartsWith($"{JwtBearerDefaults.AuthenticationScheme} ", StringComparison.OrdinalIgnoreCase)
            ? header[(JwtBearerDefaults.AuthenticationScheme.Length + 1)..]
            : null;
    }
}
