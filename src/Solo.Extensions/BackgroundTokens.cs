using System.Security.Cryptography;
using System.Text;

namespace Solo.Extensions;

/// <summary>
/// The credential background work sends to core's REST API: a grant id and an
/// expiry, signed with a key that exists only in this process.
/// </summary>
/// <remarks>
/// <para>Nothing is stored. The token names the grant, and the authentication
/// handler looks the grant up on every request, so revoking it — or signing its
/// user out everywhere — stops the very next call rather than after expiry. The
/// expiry only bounds a token copied out of a log.</para>
///
/// <para>The key is random per process, so a token can't outlive a restart or
/// be minted anywhere but here, and there is no secret to configure or leak.
/// One instance is the deployment model; a second one would need the key
/// shared.</para>
/// </remarks>
public sealed class BackgroundTokens(TimeProvider time)
{
    /// <summary>
    /// Authentication scheme and identity type of a background request. Here
    /// rather than in Solo.Api so the host half can recognise one too.
    /// </summary>
    public const string Scheme = "SoloBackground";

    /// <summary>Distinct from sessions' <c>solo_</c>, which this would otherwise match.</summary>
    public const string Prefix = "solobg_";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public static bool IsBackgroundToken(string? token) =>
        token?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public string Mint(Guid grantId)
    {
        var payload = $"{grantId:N}.{(time.GetUtcNow() + Lifetime).ToUnixTimeSeconds()}";
        return $"{Prefix}{payload}.{Sign(payload)}";
    }

    /// <summary>The grant a token names, or null when it is malformed, forged or expired.</summary>
    public Guid? Read(string? token)
    {
        if (!IsBackgroundToken(token))
        {
            return null;
        }

        var parts = token![Prefix.Length..].Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var payload = $"{parts[0]}.{parts[1]}";
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Sign(payload)), Encoding.ASCII.GetBytes(parts[2])))
        {
            return null;
        }

        if (!long.TryParse(parts[1], out var expires) ||
            DateTimeOffset.FromUnixTimeSeconds(expires) <= time.GetUtcNow())
        {
            return null;
        }

        return Guid.TryParseExact(parts[0], "N", out var grantId) ? grantId : null;
    }

    private string Sign(string payload) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
