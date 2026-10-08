using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Realtime;
using Solo.Api.Services;
using Solo.Data;
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
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<SessionDto>> Create(
        [FromBody] CreateSessionRequest? request,
        [FromServices] CurrentUserAccessor currentUser,
        [FromServices] SoloDbContext db,
        [FromServices] IConfiguration config,
        CancellationToken ct)
    {
        // Only a provider-issued credential proves the user is still who they
        // say they are. Nothing Solo or an extension issued earlier — a
        // session, an MCP token — may buy a session, or a leaked one would be
        // renewable forever (see CredentialKind).
        if (!User.IsProviderSignIn())
        {
            return Forbid();
        }

        if (currentUser.User is not { } user)
        {
            return Forbid();
        }

        var (session, token) = AuthSessionMinter.Mint(user.Id, request?.Description, config);
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return new SessionDto(token, session.ExpiresAt, user.ToDto());
    }

    /// <summary>
    /// The caller's live sessions, most recently used first: the signed-in
    /// devices list. A browser signed in with the provider holds no session,
    /// so there it lists the desktop apps and nothing marked current.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AuthSessionDto>>> List(
        [FromServices] CurrentUserAccessor currentUser,
        [FromServices] SoloDbContext db,
        CancellationToken ct)
    {
        if (!User.IsSoloSignIn())
        {
            return CredentialCannotManageSessions();
        }

        var userId = currentUser.User!.Id;
        var current = CurrentSessionId();
        var now = DateTimeOffset.UtcNow;
        return await db.AuthSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastUsedAt)
            .Select(s => new AuthSessionDto(
                s.Id, s.Description, s.CreatedAt, s.LastUsedAt, s.ExpiresAt, s.Id == current))
            .ToListAsync(ct);
    }

    /// <summary>Signs one of the caller's devices out.</summary>
    [HttpDelete("{sessionId:guid}")]
    public async Task<IActionResult> Revoke(
        Guid sessionId,
        [FromServices] CurrentUserAccessor currentUser,
        [FromServices] SoloDbContext db,
        [FromServices] LiveAccess live,
        CancellationToken ct)
    {
        if (!User.IsSoloSignIn())
        {
            return CredentialCannotManageSessions();
        }

        // Scoped to the caller, so another user's session id is a 404, not a
        // way to sign them out.
        var userId = currentUser.User!.Id;
        var revoked = await db.AuthSessions
            .Where(s => s.Id == sessionId && s.UserId == userId)
            .ExecuteDeleteAsync(ct);
        if (revoked == 0)
        {
            return NotFound();
        }

        live.EndSessions([sessionId]);
        return NoContent();
    }

    /// <summary>
    /// Signs every other device out: revokes all the caller's sessions except
    /// the one making this request, which ordinary sign-out covers.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> RevokeOthers(
        [FromServices] CurrentUserAccessor currentUser,
        [FromServices] SoloDbContext db,
        [FromServices] LiveAccess live,
        CancellationToken ct)
    {
        if (!User.IsSoloSignIn())
        {
            return CredentialCannotManageSessions();
        }

        var userId = currentUser.User!.Id;
        var others = db.AuthSessions.Where(s => s.UserId == userId);
        if (CurrentSessionId() is { } current)
        {
            others = others.Where(s => s.Id != current);
        }

        var revoked = await others.Select(s => s.Id).ToListAsync(ct);
        await db.AuthSessions.Where(s => revoked.Contains(s.Id)).ExecuteDeleteAsync(ct);
        live.EndSessions(revoked);
        return NoContent();
    }

    /// <summary>Sign-out: revokes the session that authenticated this request.</summary>
    [HttpDelete("current")]
    public async Task<IActionResult> RevokeCurrent(
        [FromServices] SoloDbContext db,
        CancellationToken ct)
    {
        // No-op for OIDC-authenticated callers: there is nothing to revoke.
        if (CurrentSessionId() is { } sessionId)
        {
            await db.AuthSessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);
        }

        return NoContent();
    }

    private Guid? CurrentSessionId() =>
        Guid.TryParse(User.FindFirst(AuthConstants.SessionIdClaim)?.Value, out var id) ? id : null;

    /// <summary>
    /// Credentials don't manage credentials. An MCP token or any other
    /// extension credential could otherwise sign its owner out of every
    /// device, or read which ones they use.
    /// </summary>
    private ObjectResult CredentialCannotManageSessions() => Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Sign in to manage devices",
        detail: "Signed-in devices can only be managed from Solo itself, in a browser or the desktop app.");
}
