using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Solo.Shared.Hubs;

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
    /// The bearer credential of a request: the Authorization header, or the
    /// <c>access_token</c> query parameter on the SignalR hub path — the same
    /// two places the OIDC scheme reads.
    /// </summary>
    public static string? FromRequest(HttpRequest request)
    {
        string header = request.Headers.Authorization.ToString();
        if (header.StartsWith($"{JwtBearerDefaults.AuthenticationScheme} ", StringComparison.OrdinalIgnoreCase))
        {
            return header[(JwtBearerDefaults.AuthenticationScheme.Length + 1)..];
        }

        if (request.Path.StartsWithSegments(BoardHubConstants.Path) &&
            request.Query["access_token"] is { Count: > 0 } token)
        {
            return token.ToString();
        }

        return null;
    }
}
