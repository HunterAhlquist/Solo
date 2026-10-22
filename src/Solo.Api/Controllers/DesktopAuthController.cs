using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
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
/// <item><c>POST /desktop-auth</c> (browser, from Google) — only with
/// <c>Auth:DesktopRedirect</c>: where Google's sign-in returns the ID token
/// and state, so the page then calls complete itself.</item>
/// <item><c>POST complete</c> (browser, provider credential) — pins the
/// verified user to the flow and returns a one-time code to loopback.</item>
/// <item><c>POST redeem</c> (shell, anonymous) — exchanges that code plus the
/// PKCE verifier for a freshly minted <c>solo_</c> session, exactly once.</item>
/// </list>
/// </summary>
[Route("api/auth/desktop")]
// Each step is logged, without its state or credential: a desktop sign-in
// crosses two processes and Google, and "it asked me twice" is otherwise
// impossible to reconstruct.
public sealed partial class DesktopAuthController(
    DesktopSignInFlows flows,
    IConfiguration config,
    ILogger<DesktopAuthController> logger) : ApiControllerBase
{
    /// <summary>
    /// Google's sign-in button opens a popup window: in Safari — including the
    /// macOS desktop app's sign-in sheet — a second window, after a click on a
    /// page of ours. Redirect mode goes straight to Google's sign-in in the
    /// same window instead, but Google only returns to an authorized redirect
    /// URI, so an instance turns it on once its <c>/desktop-auth</c> address
    /// is registered. The web app's own sign-in is unaffected either way.
    /// </summary>
    private bool RedirectEnabled => config.GetValue<bool>("Auth:DesktopRedirect");

    /// <summary>How the sign-in page should run Google's button.</summary>
    [HttpGet("options")]
    [AllowAnonymous]
    public ActionResult<DesktopAuthOptions> Options() => new DesktopAuthOptions(RedirectEnabled);

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
            logger.LogInformation("Desktop sign-in page asked for an unknown, expired or finished flow.");
            return NotFound("This sign-in link has expired. Start again from the Solo app.");
        }

        logger.LogInformation("Desktop sign-in page served.");
        Response.Headers.CacheControl = "no-store";
        return File("desktop-auth.html", "text/html; charset=utf-8");
    }

    /// <summary>
    /// With redirect mode the sign-in page sends the browser straight to
    /// Google's OpenID Connect sign-in, which posts the ID token and the flow's
    /// state back here, to the page's own address. The page comes back with
    /// both attached and finishes through <see cref="Complete"/>.
    /// </summary>
    /// <remarks>
    /// Nothing here trusts what was posted: complete validates the token like
    /// any bearer token and requires its nonce to be the flow's state, so a
    /// token posted here from anywhere else cannot finish a sign-in. Only
    /// values shaped like a token, a state and an error code are echoed back.
    /// </remarks>
    [HttpPost("/desktop-auth")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> RedirectReturn(
        [FromForm(Name = "id_token")] string? idToken,
        [FromForm] string? state,
        [FromForm] string? error,
        [FromServices] IWebHostEnvironment env,
        CancellationToken ct)
    {
        if (!RedirectEnabled)
        {
            logger.LogWarning("Desktop sign-in redirect refused: redirect mode is off.");
            return NotFound("Start sign-in again from the Solo app.");
        }

        if (error is not null && ErrorCodeShape().IsMatch(error))
        {
            // Cancelled on Google's page, or refused there.
            logger.LogInformation("Desktop sign-in returned from Google with {Error}.", error);
            return await ServePageAsync(env, ct, ("solo-error", error));
        }

        if (idToken is null || !JwtShape().IsMatch(idToken) || state is null || !StateShape().IsMatch(state))
        {
            logger.LogWarning("Desktop sign-in redirect refused: no token or state came back.");
            return NotFound("Start sign-in again from the Solo app.");
        }

        logger.LogInformation("Desktop sign-in returned from Google's redirect.");
        return await ServePageAsync(env, ct, ("solo-credential", idToken), ("solo-state", state));
    }

    // The sign-in page with values for its script in meta tags: the CSP
    // allows no inline script.
    private async Task<IActionResult> ServePageAsync(
        IWebHostEnvironment env, CancellationToken ct, params (string Name, string Value)[] values)
    {
        var file = env.WebRootFileProvider.GetFileInfo("desktop-auth.html");
        string page;
        await using (var stream = file.CreateReadStream())
        using (var reader = new StreamReader(stream))
        {
            page = await reader.ReadToEndAsync(ct);
        }

        var tags = string.Concat(values.Select(v =>
            "<meta name=\"" + v.Name + "\" content=\"" + HtmlEncoder.Default.Encode(v.Value) + "\" />\n"));
        page = page.Replace("</head>", tags + "</head>", StringComparison.Ordinal);

        Response.Headers.CacheControl = "no-store";
        return Content(page, "text/html; charset=utf-8");
    }

    // Three base64url segments: anything else is not an ID token.
    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$")]
    private static partial Regex JwtShape();

    // A flow's state as DesktopSignInFlows mints it, with room to spare.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex StateShape();

    // An OAuth error code such as access_denied.
    [GeneratedRegex(@"^[a-z_]{1,64}$")]
    private static partial Regex ErrorCodeShape();

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

        // The sign-in page asks Google to put the flow's state in the ID
        // token's nonce. A credential issued for any other sign-in — another
        // person's, posted into the redirect endpoint — cannot finish this one.
        if (User.FindFirst("nonce")?.Value != request.State)
        {
            logger.LogWarning("Desktop sign-in refused: the credential was issued for another flow.");
            return Forbid();
        }

        if (!flows.TryComplete(request.State, user.Id, out var completion))
        {
            logger.LogInformation("Desktop sign-in completion for an unknown, expired or finished flow.");
            return NotFound();
        }

        logger.LogInformation("Desktop sign-in completed for user {UserId}.", user.Id);
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

/// <summary>How <c>/desktop-auth</c> runs Google's button: in a popup, or by redirect.</summary>
public sealed record DesktopAuthOptions(bool Redirect);
