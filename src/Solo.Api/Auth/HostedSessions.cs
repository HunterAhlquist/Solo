using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Solo.Api.Auth;

/// <summary>
/// The credential an app hands to the web client it shows in a web view of its
/// own (<c>HostShell</c> in the client): the same person as the app's session,
/// for a short while, and good for nothing else.
/// </summary>
/// <remarks>
/// <para>An app's own session lasts <c>Auth:SessionDays</c> and slides on use.
/// Handed to a page, it sat in script-readable storage beside whatever an
/// extension's page runs, so anything that got script into one page had a
/// credential good for a month and renewed by using it. This is what the page
/// is given instead.</para>
///
/// <para>It is a sealed note, not a row: which session it stands for, and when
/// it ends. Nothing is stored, so there is nothing to list, clean up or
/// forget to revoke, and each of its rules is one it can't break:</para>
/// <list type="bullet">
/// <item>It ends when the note says, used or not. Nothing extends it; the app
/// asks for another with its own session, which the page never sees.</item>
/// <item>It is read against the session it stands for on every request, so it
/// dies with that session: revoked, expired, or its person removed.</item>
/// <item>The identity it gives is its own kind (<see cref="CredentialKind"/>),
/// which the rules about minting and managing credentials don't admit. It
/// can't buy a session, another of itself, or the right to revoke anything.</item>
/// <item>Using it doesn't count as using the session: a page left running
/// doesn't keep an abandoned session alive.</item>
/// </list>
/// </remarks>
public sealed class HostedSessions(IDataProtectionProvider protection, IConfiguration config, TimeProvider time)
{
    /// <summary>
    /// Under <see cref="SessionTokens.Prefix"/>, so the request is routed to the
    /// session scheme, which tells the two apart.
    /// </summary>
    public const string Prefix = SessionTokens.Prefix + "hosted_";

    private readonly IDataProtector _protector = protection.CreateProtector("Solo.Auth.HostedSession");

    /// <summary>
    /// How long one lasts. An hour: long enough that an app renewing ahead of
    /// time does so a handful of times a day, short enough that one read out
    /// of a page is soon worth nothing.
    /// </summary>
    public TimeSpan Lifetime
    {
        get
        {
            var minutes = config.GetValue("Auth:HostedSessionMinutes", 60.0);
            return TimeSpan.FromMinutes(minutes > 0 ? minutes : 60);
        }
    }

    public static bool IsHostedToken(string? token) =>
        token?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public (string Token, DateTimeOffset ExpiresAt) Mint(Guid sessionId)
    {
        var expiresAt = time.GetUtcNow() + Lifetime;
        var note = string.Create(CultureInfo.InvariantCulture, $"{sessionId:N}.{expiresAt.ToUnixTimeMilliseconds()}");
        return (Prefix + _protector.Protect(note), expiresAt);
    }

    /// <summary>The session a token stands for, if it is one of ours and hasn't ended.</summary>
    public Guid? SessionOf(string token)
    {
        if (!IsHostedToken(token))
        {
            return null;
        }

        string note;
        try
        {
            note = _protector.Unprotect(token[Prefix.Length..]);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }

        var parts = note.Split('.');
        return parts.Length == 2 &&
               Guid.TryParseExact(parts[0], "N", out var sessionId) &&
               long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ends) &&
               time.GetUtcNow().ToUnixTimeMilliseconds() < ends
            ? sessionId
            : null;
    }
}
