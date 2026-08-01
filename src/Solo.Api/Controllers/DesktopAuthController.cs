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
/// Browser sign-in for the desktop shell, device-code style. Electron cannot
/// host Google's sign-in pages, so the shell opens <c>/desktop-auth</c> — a
/// page on this instance's own, provider-registered origin — in the user's
/// real browser, and polls for the result:
/// <list type="number">
/// <item><c>POST start</c> (shell, anonymous) — opens a flow: the state names
/// it in the browser URL, the device code stays in the shell.</item>
/// <item><c>GET /desktop-auth?state=…</c> (browser) — the sign-in page; runs
/// the ordinary provider flow and calls complete.</item>
/// <item><c>POST complete</c> (browser, provider credential) — pins the
/// verified user to the flow, single-use.</item>
/// <item><c>POST poll</c> (shell, anonymous) — redeems the device code for a
/// freshly minted <c>solo_</c> session, exactly once.</item>
/// </list>
/// </summary>
[Route("api/auth/desktop")]
public sealed class DesktopAuthController(DesktopSignInFlows flows) : ApiControllerBase
{
    [HttpPost("start")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public ActionResult<DesktopAuthFlowDto> Start([FromBody] DesktopAuthStartRequest? request)
    {
        // Key the per-client pending cap on the caller's IP. UseForwardedHeaders
        // has already resolved the real client address from a trusted proxy, so
        // this matches how the rate limiter partitions callers.
        var clientKey = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (flows.Start(request?.Description, clientKey) is not { } flow)
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
    [EnableRateLimiting(RateLimitPolicies.AuthPoll)]
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
    public IActionResult Complete(
        [FromBody] DesktopAuthCompleteRequest request,
        [FromServices] CurrentUserAccessor currentUser)
    {
        // Only a provider-issued credential proves the user is still who they
        // say they are; a session token must never buy another session.
        if (User.HasClaim(c => c.Type == AuthConstants.SessionIdClaim))
        {
            return Forbid();
        }

        if (currentUser.User is not { } user)
        {
            return Forbid();
        }

        if (!flows.TryComplete(request.State, user.Id))
        {
            return NotFound();
        }

        return NoContent();
    }

    [HttpPost("poll")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AuthPoll)]
    public async Task<ActionResult<SessionDto>> Poll(
        [FromBody] DesktopAuthPollRequest request,
        [FromServices] SoloDbContext db,
        [FromServices] IConfiguration config,
        CancellationToken ct)
    {
        switch (flows.Poll(request.DeviceCode, out var userId, out var description))
        {
            case DesktopSignInFlows.PollStatus.Pending:
                return Accepted();

            case DesktopSignInFlows.PollStatus.Completed:
                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
                if (user is null)
                {
                    // Deprovisioned between completing and polling.
                    return NotFound();
                }

                var (session, token) = AuthSessionMinter.Mint(user.Id, description, config);
                db.AuthSessions.Add(session);
                await db.SaveChangesAsync(ct);
                return new SessionDto(token, session.ExpiresAt, user.ToDto());

            default:
                return NotFound();
        }
    }
}
