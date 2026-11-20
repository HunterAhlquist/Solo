using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solo.Data;
using Solo.Data.Entities;

namespace Solo.Api.Auth;

/// <summary>
/// Authenticates <c>solo_</c> session tokens (see <see cref="SessionTokens"/>),
/// from the Authorization header (desktop) or the web app's cookie
/// (<see cref="SessionCookie"/>).
/// The principal carries the same raw claim names an OIDC token would, so user
/// resolution downstream (<see cref="UserProvisioningService"/>) is oblivious
/// to which credential arrived. Expiry slides on use: any request more than an
/// hour after the last touch pushes the horizon out again, so only a session
/// idle for the whole lifetime dies.
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration config)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly TimeSpan SlideThrottle = TimeSpan.FromHours(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = SessionTokens.FromRequest(Request);
        var cookie = SessionCookie.FromRequest(Request);
        if (!SessionTokens.IsSessionToken(token))
        {
            if (cookie is null)
            {
                return AuthenticateResult.NoResult();
            }

            token = cookie.Value.Token;
        }

        var db = Context.RequestServices.GetRequiredService<SoloDbContext>();
        var now = DateTimeOffset.UtcNow;

        // What an app hands a page it hosts: not a session, but a short-lived
        // stand-in for one (HostedSessions). It is only as good as the session
        // it names, read here each time, and using it is not using that
        // session — nothing slides.
        if (HostedSessions.IsHostedToken(token))
        {
            var hosted = Context.RequestServices.GetRequiredService<HostedSessions>();
            var parent = hosted.SessionOf(token!) is { } parentId
                ? await db.AuthSessions.AsNoTracking().Include(s => s.User)
                    .FirstOrDefaultAsync(s => s.Id == parentId, Context.RequestAborted)
                : null;
            return parent is null || parent.ExpiresAt <= now
                ? AuthenticateResult.Fail("Unknown or expired hosted session token.")
                : Success(parent.User, AuthConstants.HostedAuthenticationType,
                    new Claim(AuthConstants.HostedParentClaim, parent.Id.ToString()));
        }

        var hash = SessionTokens.Hash(token!);
        var session = await db.AuthSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.TokenHash == hash, Context.RequestAborted);
        if (session is null || session.ExpiresAt <= now)
        {
            return AuthenticateResult.Fail("Unknown or expired session token.");
        }

        // Sliding expiry, throttled so busy sessions don't write on every request.
        if (now - session.LastUsedAt > SlideThrottle)
        {
            session.LastUsedAt = now;
            session.ExpiresAt = now + SessionLifetime(config);
            await db.SaveChangesAsync(Context.RequestAborted);

            // The cookie's own expiry doesn't slide with the session's, so a
            // persistent one would lapse 30 days after sign-in however often
            // it was used. Re-issued on the same throttle.
            if (cookie is { } used && token == used.Token)
            {
                SessionCookie.Append(Response, used.Token, used.Persistent, session.ExpiresAt);
            }
        }

        return Success(session.User, Scheme.Name, new Claim(AuthConstants.SessionIdClaim, session.Id.ToString()));
    }

    /// <summary>
    /// The principal for a person, with the claim that says which session they
    /// came by. <paramref name="kind"/> is the identity's authentication type,
    /// which is what tells a session from what was issued on one's behalf
    /// (<see cref="CredentialKind"/>).
    /// </summary>
    private AuthenticateResult Success(User user, string kind, Claim session)
    {
        var claims = new List<Claim>
        {
            new("iss", user.Issuer),
            new("sub", user.Subject),
            session,
        };
        if (user.Email.Length > 0)
        {
            claims.Add(new Claim("email", user.Email));
        }

        if (user.DisplayName is not null)
        {
            claims.Add(new Claim("name", user.DisplayName));
        }

        var identity = new ClaimsIdentity(claims, kind, nameType: "name", roleType: "role");
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    public static TimeSpan SessionLifetime(IConfiguration config)
    {
        var days = config.GetValue("Auth:SessionDays", 30);
        return TimeSpan.FromDays(days < 1 ? 30 : days);
    }
}
