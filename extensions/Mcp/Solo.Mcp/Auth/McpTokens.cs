using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Solo.Mcp.Auth;

/// <summary>
/// The opaque bearer tokens behind <see cref="Data.McpToken"/>.
/// </summary>
/// <remarks>
/// <para>The prefix is deliberately <em>not</em> in core's <c>solo_</c> family.
/// Core routes any <c>solo_</c> credential to its session scheme, so a token
/// borrowing that prefix would be claimed by a handler that knows nothing about
/// it — the extension would be fighting core for its own credential. Starting
/// somewhere else means core's classifier ignores it and this extension's
/// authentication is the only thing that ever looks at it.</para>
///
/// <para>32 bytes of CSPRNG output, base64url-encoded. Only <see cref="Hash"/>
/// is ever persisted.</para>
/// </remarks>
public static class McpTokens
{
    public const string Prefix = "solomcp_";

    /// <summary>The scheme name in the Authorization header.</summary>
    private const string BearerScheme = "Bearer";

    public static string Generate() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool IsMcpToken(string? token) =>
        token?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>The bearer credential of a request, or null when there is none.</summary>
    public static string? FromRequest(HttpRequest request)
    {
        string header = request.Headers.Authorization.ToString();
        return header.StartsWith($"{BearerScheme} ", StringComparison.OrdinalIgnoreCase)
            ? header[(BearerScheme.Length + 1)..]
            : null;
    }
}
