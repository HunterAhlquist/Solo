namespace Solo.Client.Api;

/// <summary>
/// Shapes served by the MCP extension's own endpoints under
/// <c>/api/ext/mcp/…</c>.
/// </summary>
/// <remarks>
/// Declared here rather than in <c>Solo.Shared</c> on purpose: MCP is an
/// extension, and core knows nothing about it. These records are this client
/// acting as a consumer of a package's API, exactly as an outside tool would —
/// which is also why every call that uses them tolerates a 404, the shape an
/// instance takes when the package is not installed.
/// </remarks>
public sealed record McpTokenDto(
    Guid Id,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string? RevokedReason);

/// <summary>A freshly minted token. <c>Value</c> is readable exactly once.</summary>
public sealed record McpTokenSecretDto(McpTokenDto Token, string Value);

public sealed record McpTokenStatusDto(
    McpTokenDto? Active,
    IReadOnlyList<McpTokenDto> History);

public sealed record McpTokenAuditDto(
    Guid Id,
    string? UserEmail,
    string? UserDisplayName,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string? RevokedReason,
    IReadOnlyList<int> Usage);

public sealed record McpTokenAdminDto(
    DateTimeOffset WindowStart,
    IReadOnlyList<McpTokenAuditDto> Tokens);

public sealed record RevokeAllMcpTokensResultDto(int Revoked);

/// <summary>What the instance's MCP endpoint offers; also how the client detects it exists.</summary>
public sealed record McpInfoDto(
    string ProtocolVersion,
    string ServerVersion,
    IReadOnlyList<McpToolInfoDto> Tools);

public sealed record McpToolInfoDto(string Name, string Title, string Description);
