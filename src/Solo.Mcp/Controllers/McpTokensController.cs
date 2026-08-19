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
public sealed class McpTokensController(McpDbContext db) : ControllerBase
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

        await RevokeAsync(
            db.Tokens.Where(t =>
                t.Issuer == caller.Issuer && t.Subject == caller.Subject && t.RevokedAt == null),
            McpTokenRevocation.Regenerated,
            ct);

        var value = McpTokens.Generate();
        var token = new McpToken
        {
            TokenHash = McpTokens.Hash(value),
            Issuer = caller.Issuer,
            Subject = caller.Subject,
            Email = caller.Email,
            DisplayName = caller.DisplayName,
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

        await RevokeAsync(
            db.Tokens.Where(t =>
                t.Issuer == caller.Issuer && t.Subject == caller.Subject && t.RevokedAt == null),
            McpTokenRevocation.RevokedByOwner,
            ct);
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

        var revoked = await RevokeAsync(
            db.Tokens.Where(t => t.RevokedAt == null), McpTokenRevocation.RevokedByAdmin, ct);

        logger.LogWarning(
            "Emergency revoke-all cut off {Count} MCP token(s), at the request of {User}.",
            revoked, User.Identity?.Name);

        return new RevokeAllMcpTokensResultDto(revoked);
    }

    // -----------------------------------------------------------------------

    private async Task<int> RevokeAsync(
        IQueryable<McpToken> tokens, McpTokenRevocation reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // Set-based: revoke-all must not load every token on the instance, and
        // the single-token case is the same statement with a narrower predicate.
        return await tokens.ExecuteUpdateAsync(
            update => update
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.RevokedReason, reason),
            ct);
    }

    /// <summary>
    /// The caller's OIDC identity — the same issuer/subject pair core keys users
    /// on, which is why tokens are stored against it rather than a user id this
    /// package has no business knowing.
    /// </summary>
    private McpCaller? Caller()
    {
        var issuer = User.FindFirstValue("iss");
        var subject = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return issuer is null || subject is null
            ? null
            : new McpCaller(
                issuer,
                subject,
                User.FindFirstValue("email") ?? User.FindFirstValue(ClaimTypes.Email),
                User.FindFirstValue("name") ?? User.FindFirstValue(ClaimTypes.Name));
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

    private sealed record McpCaller(string Issuer, string Subject, string? Email, string? DisplayName);
}

// ---------------------------------------------------------------------------
// The extension's own API shapes. Deliberately not in Solo.Shared: this package
// owns its endpoints, so it owns their contract too, and core stays unaware the
// feature exists.
// ---------------------------------------------------------------------------

/// <summary>One token, without the secret.</summary>
public sealed record McpTokenDto(
    Guid Id,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    /// <summary>Regenerated, RevokedByOwner or RevokedByAdmin. Null while live.</summary>
    string? RevokedReason);

/// <summary>
/// A freshly minted token. <see cref="Value"/> is returned exactly once — the
/// server keeps only a SHA-256, so a lost token is regenerated, never recovered.
/// </summary>
public sealed record McpTokenSecretDto(McpTokenDto Token, string Value);

public sealed record McpTokenStatusDto(
    /// <summary>The live token, or null when the caller has none.</summary>
    McpTokenDto? Active,
    /// <summary>Every token this account has held, newest first — including revoked ones.</summary>
    IReadOnlyList<McpTokenDto> History);

public sealed class CreateMcpTokenRequest
{
    /// <summary>Owner-supplied label, e.g. "Claude Code on the laptop".</summary>
    [StringLength(200)]
    public string? Description { get; init; }
}

/// <summary>
/// Admin audit of one token. Carries no hash and no secret: an admin can see
/// that a token exists, when it was created and cut off, and how much it is
/// being used — never what it is.
/// </summary>
public sealed record McpTokenAuditDto(
    Guid Id,
    string? UserEmail,
    string? UserDisplayName,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string? RevokedReason,
    /// <summary>Requests per hour over the audit window, oldest bucket first.</summary>
    IReadOnlyList<int> Usage);

public sealed record McpTokenAdminDto(
    /// <summary>Start of the oldest usage bucket; each entry in Usage covers one hour from here.</summary>
    DateTimeOffset WindowStart,
    IReadOnlyList<McpTokenAuditDto> Tokens);

/// <summary>Emergency revoke-all confirmation: the admin retypes a fixed phrase.</summary>
public sealed class RevokeAllMcpTokensRequest
{
    [Required]
    public required string Confirm { get; init; }
}

public sealed record RevokeAllMcpTokensResultDto(int Revoked);

internal static class McpTokenMapping
{
    public static McpTokenDto ToDto(this McpToken token) => new(
        token.Id,
        token.Description,
        token.CreatedAt,
        token.LastUsedAt,
        token.RevokedAt,
        token.RevokedReason?.ToString());
}
