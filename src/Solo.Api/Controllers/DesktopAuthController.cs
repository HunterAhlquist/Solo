using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Services;
using Solo.Data;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>
/// Browser sign-in for the desktop shell. Electron cannot
/// host Google's sign-in pages, so the shell opens <c>/desktop-auth</c> — a
/// page on this instance's own, provider-registered origin — in the user's
/// real browser. A loopback callback and PKCE bind the result to the desktop
/// process that opened the flow:
/// <list type="number">
/// <item><c>POST start</c> (shell, anonymous) — opens a flow with the shell's
/// loopback port and PKCE challenge.</item>
/// <item><c>GET /desktop-auth?state=…</c> (browser) — the sign-in page; runs
/// the ordinary provider flow and calls complete.</item>
/// <item><c>POST complete</c> (browser, provider credential) — pins the
/// verified user to the flow and returns a one-time code to loopback.</item>
/// <item><c>POST redeem</c> (shell, anonymous) — exchanges that code plus the
/// PKCE verifier for a freshly minted <c>solo_</c> session, exactly once.</item>
/// </list>
/// </summary>
[Route("api/auth/desktop")]
public sealed class DesktopAuthController(DesktopSignInFlows flows) : ApiControllerBase
{
    [HttpPost("start")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public ActionResult<DesktopAuthFlowDto> Start([FromBody] DesktopAuthStartRequest request)
    {
        // Key the per-client pending cap on the caller's IP. UseForwardedHeaders
        // has already resolved the real client address from a trusted proxy, so
        // this matches how the rate limiter partitions callers.
        var clientKey = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (flows.Start(
                request.Description,
                request.CodeChallenge,
                request.LoopbackPort,
                clientKey) is not { } flow)
        {
            return Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many pending sign-ins",
                detail: "Try again in a few minutes.");
        }

        return flow;
    }

    /// <summary>
    /// The page the shell opens in the system browser. Served only while its
    /// flow is live, so a stale link says so instead of dead-ending in Google.
    /// </summary>
    [HttpGet("/desktop-auth")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public IActionResult SignInPage([FromQuery] string? state)
    {
        if (!flows.IsPending(state))
        {
            return NotFound("This sign-in link has expired. Start again from the Solo app.");
        }

        Response.Headers.CacheControl = "no-store";
        return File("desktop-auth.html", "text/html; charset=utf-8");
    }

    /// <summary>
    /// Called by the sign-in page with the provider credential as its bearer
    /// token — the same validation path as any API call proves who signed in.
    /// </summary>
    [HttpPost("complete")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public ActionResult<DesktopAuthCompletionDto> Complete(
        [FromBody] DesktopAuthCompleteRequest request,
        [FromServices] CurrentUserAccessor currentUser)
    {
        // Only a provider-issued credential proves the user is still who they
        // say they are; nothing Solo or an extension issued may buy a session
        // (see CredentialKind).
        if (!User.IsProviderSignIn())
        {
            return Forbid();
        }

        if (currentUser.User is not { } user)
        {
            return Forbid();
        }

        if (!flows.TryComplete(request.State, user.Id, out var completion))
        {
            return NotFound();
        }

        return completion!;
    }

    [HttpPost("redeem")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<SessionDto>> Redeem(
        [FromBody] DesktopAuthRedeemRequest request,
        [FromServices] SoloDbContext db,
        [FromServices] IConfiguration config,
        CancellationToken ct)
    {
        if (!flows.TryRedeem(
                request.State,
                request.AuthorizationCode,
                request.CodeVerifier,
                out var userId,
                out var description))
        {
            return NotFound();
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            // Deprovisioned between browser completion and code redemption.
            return NotFound();
        }

        var (session, token) = AuthSessionMinter.Mint(user.Id, description, config);
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return new SessionDto(token, session.ExpiresAt, user.ToDto());
    }
}
