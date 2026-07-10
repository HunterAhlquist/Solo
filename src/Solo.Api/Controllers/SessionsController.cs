using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>
/// Long-lived API sessions for clients that cannot renew provider tokens
/// silently — the desktop app. A fresh OIDC ID token buys a <c>solo_</c>
/// session token whose expiry slides on use (see SessionAuthenticationHandler);
/// signing out revokes it.
/// </summary>
[Route("api/auth/sessions")]
[Authorize]
public sealed class SessionsController : ApiControllerBase
{
    [HttpPost]
    public async Task<ActionResult<SessionDto>> Create(
        [FromBody] CreateSessionRequest? request,
        [FromServices] CurrentUserAccessor currentUser,
        [FromServices] SoloDbContext db,
        [FromServices] IConfiguration config,
        CancellationToken ct)
    {
        // Only a provider-issued credential proves the user is still who they
        // say they are. A session token must never mint another session — a
        // leaked token would otherwise be renewable forever.
        if (User.HasClaim(c => c.Type == AuthConstants.SessionIdClaim))
        {
            return Forbid();
        }

        if (currentUser.User is not { } user)
        {
            return Forbid();
        }

        var token = SessionTokens.Generate();
        var session = new AuthSession
        {
            UserId = user.Id,
            TokenHash = SessionTokens.Hash(token),
            Description = request?.Description,
            ExpiresAt = DateTimeOffset.UtcNow + SessionAuthenticationHandler.SessionLifetime(config),
        };
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return new SessionDto(token, session.ExpiresAt, user.ToDto());
    }

    /// <summary>Sign-out: revokes the session that authenticated this request.</summary>
    [HttpDelete("current")]
    public async Task<IActionResult> RevokeCurrent(
        [FromServices] SoloDbContext db,
        CancellationToken ct)
    {
        // No-op for OIDC-authenticated callers: there is nothing to revoke.
        if (Guid.TryParse(User.FindFirst(AuthConstants.SessionIdClaim)?.Value, out var sessionId))
        {
            await db.AuthSessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);
        }

        return NoContent();
    }
}
