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
