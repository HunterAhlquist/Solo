using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Solo.Extensibility;
using Solo.Mcp.Auth;
using Solo.Mcp.Data;

namespace Solo.Mcp.Controllers;

/// <summary>
/// Per-user MCP tokens: generate, rotate, revoke — and the admin audit.
/// </summary>
/// <remarks>
/// <para>One live token per account. Generating a second revokes the first as
/// <see cref="McpTokenRevocation.Regenerated"/> rather than deleting it, so
/// "I rotated my token" stays visible in the audit and the old value stops
/// working the moment the new one exists.</para>
///
/// <para>The plaintext leaves the server exactly once, in the response to
/// <see cref="Create"/>. Everything else — this controller's own reads, the
/// admin audit, the database — is metadata only, because the database holds
/// nothing but a SHA-256.</para>
/// </remarks>
[ApiController]
[Route("tokens")]
[Authorize]
public sealed class McpTokensController(McpDbContext db, ISoloQuery query, McpUsageCounter usage) : ControllerBase
{
    /// <summary>What the emergency revoke-all asks an admin to retype.</summary>
    public const string RevokeAllConfirmation = "revoke all tokens";

    /// <summary>The caller's own tokens.</summary>
    [HttpGet]
    public async Task<ActionResult<McpTokenStatusDto>> Status(CancellationToken ct)
    {
        if (CredentialSelfService() is { } refusal)
        {
            return refusal;
        }

        if (Caller() is not { } caller)
        {
            return Forbid();
        }

        // Usage is counted in memory; write it out so last-used is current.
        await usage.FlushAsync(db, ct);
        var tokens = await db.Tokens.AsNoTracking()
            .Where(t => t.Issuer == caller.Issuer && t.Subject == caller.Subject)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        return new McpTokenStatusDto(
            tokens.FirstOrDefault(t => t.RevokedAt == null)?.ToDto(),
            tokens.Select(t => t.ToDto()).ToList());
    }

    /// <summary>
    /// Mints a token, revoking whatever the caller had before. The response is
    /// the only time the value is ever readable.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<McpTokenSecretDto>> Create(
        [FromBody] CreateMcpTokenRequest? request, CancellationToken ct)
    {
        if (CredentialSelfService() is { } refusal)
        {
            return refusal;
        }

        if (Caller() is not { } caller)
        {
            return Forbid();
        }

        // A valid sign-in is not a Solo user: any Google account can present
        // one. Only someone core provisioned may hold a token, and the profile
        // stored with it is that user's, never the raw claims — an IdP can send
        // an email it marks unverified, which core discards and this must too.
        var user = await query.CallerAsync(ct);
        if (user.UserId == Guid.Empty)
        {
            return Forbid();
        }

        await db.Tokens.Where(t =>
                t.Issuer == caller.Issuer && t.Subject == caller.Subject && t.RevokedAt == null)
            .RevokeAsync(McpTokenRevocation.Regenerated, ct);

        var value = McpTokens.Generate();
        var token = new McpToken
        {
            TokenHash = McpTokens.Hash(value),
            Issuer = caller.Issuer,
            Subject = caller.Subject,
            Email = user.Email,
            DisplayName = user.DisplayName,
            Description = request?.Description,
        };
        db.Tokens.Add(token);
        await db.SaveChangesAsync(ct);

        return new McpTokenSecretDto(token.ToDto(), value);
    }

    /// <summary>Revokes the caller's live token without minting a replacement.</summary>
    [HttpDelete("current")]
    public async Task<IActionResult> RevokeCurrent(CancellationToken ct)
    {
        if (CredentialSelfService() is { } refusal)
        {
            return refusal;
        }

        if (Caller() is not { } caller)
        {
            return Forbid();
        }

        await db.Tokens.Where(t =>
                t.Issuer == caller.Issuer && t.Subject == caller.Subject && t.RevokedAt == null)
            .RevokeAsync(McpTokenRevocation.RevokedByOwner, ct);
        return NoContent();
    }

    // -----------------------------------------------------------------------
    // Admin
    // -----------------------------------------------------------------------

    /// <summary>
    /// Every token on the instance, with a 24-hour usage series each.
    /// </summary>
    /// <remarks>
    /// Metadata only, and that is the point: an admin can see that an account
    /// holds a token, when it was created and cut off, and how hard it is being
    /// used — but the database stores only a hash, so there is nothing here for
    /// them to read even if they wanted to.
    /// </remarks>
    [HttpGet("admin")]
    [Authorize(Policy = SoloPolicies.SuperAdmin)]
    public async Task<ActionResult<McpTokenAdminDto>> Admin(CancellationToken ct)
    {
        const int hours = 24;
        var now = DateTimeOffset.UtcNow;
        var windowStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero)
            .AddHours(-(hours - 1));

        await usage.FlushAsync(db, ct);
        var tokens = await db.Tokens.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        var buckets = await db.Usage.AsNoTracking()
            .Where(u => u.Hour >= windowStart)
            .ToListAsync(ct);
        var byToken = buckets
            .GroupBy(u => u.TokenId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(u => u.Hour, u => u.Count));

        var rows = tokens.Select(token =>
        {
            var series = byToken.GetValueOrDefault(token.Id);
            // Dense: a graph with holes for quiet hours would read as missing
            // data rather than as no traffic.
            var usage = Enumerable.Range(0, hours)
                .Select(i => series?.GetValueOrDefault(windowStart.AddHours(i)) ?? 0)
                .ToList();

            return new McpTokenAuditDto(
                token.Id,
                token.Email,
                token.DisplayName,
                token.Description,
                token.CreatedAt,
                token.LastUsedAt,
                token.RevokedAt,
                token.RevokedReason?.ToString(),
                usage);
        }).ToList();

        return new McpTokenAdminDto(windowStart, rows);
    }

    /// <summary>
    /// Cuts off one token — in practice, one person's access, since an account
    /// holds at most one live token.
    /// </summary>
    /// <remarks>
    /// The proportionate tool next to revoke-all: when one person's laptop is
    /// lost, or one assistant is misbehaving, an admin should not have to cut off
    /// everybody to deal with it. Idempotent — revoking an already-revoked token
    /// leaves the original reason and time intact, because the audit's account of
    /// when access actually stopped must not move.
    /// </remarks>
    [HttpPost("admin/{tokenId:guid}/revoke")]
    [Authorize(Policy = SoloPolicies.SuperAdmin)]
    public async Task<IActionResult> RevokeOne(
        Guid tokenId,
        [FromServices] ILogger<McpTokensController> logger,
        CancellationToken ct)
    {
        var token = await db.Tokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tokenId, ct);
        if (token is null)
        {
            return NotFound();
        }

        if (token.RevokedAt is not null)
        {
            return NoContent();
        }

        await db.Tokens.Where(t => t.Id == tokenId && t.RevokedAt == null)
            .RevokeAsync(McpTokenRevocation.RevokedByAdmin, ct);

        logger.LogWarning(
            "MCP token {TokenId} ({Owner}) revoked by {User}.",
            tokenId, token.Email ?? token.Subject, User.Identity?.Name);

        return NoContent();
    }

    /// <summary>
    /// Cuts off every live token on the instance at once.
    /// </summary>
    /// <remarks>
    /// The emergency lever, so it is deliberately blunt: no per-token choice, no
    /// undo, and every owner has to visit their own page to generate a new one.
    /// Guarded by retyping a fixed phrase — the same shape as the extension
    /// purge confirmation — and logged with the admin's name, because nothing
    /// else records a mass revocation.
    /// </remarks>
    [HttpPost("admin/revoke-all")]
    [Authorize(Policy = SoloPolicies.SuperAdmin)]
    public async Task<ActionResult<RevokeAllMcpTokensResultDto>> RevokeAll(
        RevokeAllMcpTokensRequest request,
        [FromServices] ILogger<McpTokensController> logger,
        CancellationToken ct)
    {
        if (!string.Equals(request.Confirm, RevokeAllConfirmation, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Confirmation does not match",
                detail: $"Type \"{RevokeAllConfirmation}\" exactly to confirm.");
        }

        var revoked = await db.Tokens.RevokeAsync(McpTokenRevocation.RevokedByAdmin, ct);

        logger.LogWarning(
            "Emergency revoke-all cut off {Count} MCP token(s), at the request of {User}.",
            revoked, User.Identity?.Name);

        return new RevokeAllMcpTokensResultDto(revoked);
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// The caller's OIDC identity — the same issuer/subject pair core keys users
    /// on, which is why tokens are stored against it rather than a user id this
    /// package has no business knowing.
    /// </summary>
    private McpCaller? Caller()
    {
        var issuer = User.FindFirstValue("iss");
        var subject = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return issuer is null || subject is null ? null : new McpCaller(issuer, subject);
    }

    /// <summary>
    /// Refuses management of credentials by a credential.
    /// </summary>
    /// <remarks>
    /// Core applies the same rule to session tokens minting sessions, for the
    /// same reason: a leaked MCP token that could mint its own replacement would
    /// survive every revocation its owner performs.
    /// </remarks>
    private ObjectResult? CredentialSelfService() =>
        User.HasClaim(c => c.Type == McpAuthentication.TokenIdClaim)
            ? Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "MCP tokens cannot manage tokens",
                detail: "Sign in to Solo in a browser to generate, rotate or revoke an MCP token.")
            : null;

    private sealed record McpCaller(string Issuer, string Subject);
}

// ---------------------------------------------------------------------------
// The extension's own API shapes. Deliberately not in Solo.Shared: this package
// owns its endpoints, so it owns their contract too, and core stays unaware the
// feature exists.
// ---------------------------------------------------------------------------
