using System.Net;
using Solo.Shared.Extensibility;

namespace Solo.Mcp.Client;

/// <summary>
/// The MCP extension's own endpoints, as its pages call them.
/// </summary>
/// <remarks>
/// A thin layer over <see cref="ISoloClientApi"/> — the host's authenticated
/// HTTP path — so this package owns its API shapes end to end: the server half
/// declares them in <c>Solo.Mcp</c>, the client half consumes them here, and
/// core knows about neither.
/// </remarks>
public sealed class McpApi(ISoloClientApi api)
{
    public const string EndpointPath = "api/ext/mcp";

    private const string TokensPath = $"{EndpointPath}/tokens";

    /// <summary>Absolute URL of the MCP endpoint, for the connection snippets.</summary>
    public Uri Endpoint => api.Resolve(EndpointPath);

    /// <summary>Absolute URL of a file in this package's <c>content/</c> folder.</summary>
    public Uri Content(string fileName) => api.Resolve($"api/extensions/mcp/content/{fileName}");

    public Task<McpInfoDto> InfoAsync(CancellationToken ct = default) =>
        api.GetAsync<McpInfoDto>($"{EndpointPath}/info", ct);

    public Task<McpTokenStatusDto> TokensAsync(CancellationToken ct = default) =>
        api.GetAsync<McpTokenStatusDto>(TokensPath, ct);

    /// <summary>
    /// Generates a token, revoking whatever this account had before. The value
    /// in the response is the only copy that will ever exist.
    /// </summary>
    public Task<McpTokenSecretDto> CreateTokenAsync(string? description, CancellationToken ct = default) =>
        api.SendAsync<McpTokenSecretDto>(HttpMethod.Post, TokensPath, new { description }, ct);

    public Task RevokeTokenAsync(CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Delete, $"{TokensPath}/current", null, ct);

    /// <summary>Every token on the instance, with its 24-hour usage series. Super admin only.</summary>
    public Task<McpTokenAdminDto> AuditAsync(CancellationToken ct = default) =>
        api.GetAsync<McpTokenAdminDto>($"{TokensPath}/admin", ct);

    /// <summary>The emergency lever: cuts off every live token on the instance.</summary>
    public Task<RevokeAllMcpTokensResultDto> RevokeAllAsync(string confirm, CancellationToken ct = default) =>
        api.SendAsync<RevokeAllMcpTokensResultDto>(
            HttpMethod.Post, $"{TokensPath}/admin/revoke-all", new { confirm }, ct);

    /// <summary>
    /// Whether this instance has the MCP server half installed and enabled.
    /// </summary>
    /// <remarks>
    /// The two halves of a package are versioned together but need not be
    /// <em>loaded</em> together: a failed migration disables the server half and
    /// its routes answer 503, while this assembly has already been downloaded and
    /// routed. So the page asks before it renders a token form pointed at
    /// nothing.
    /// </remarks>
    public async Task<McpInfoDto?> TryInfoAsync(CancellationToken ct = default)
    {
        try
        {
            return await InfoAsync(ct);
        }
        catch (SoloApiException e) when (e.StatusCode is HttpStatusCode.NotFound
                                             or HttpStatusCode.ServiceUnavailable)
        {
            return null;
        }
    }
}

// ---------------------------------------------------------------------------
// The server half's shapes, restated for the browser. Not shared through a
// common assembly on purpose: the two halves ship in one package and version as
// one, and giving them a third project to agree on would be ceremony.
// ---------------------------------------------------------------------------

public sealed record McpTokenDto(
    Guid Id,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string? RevokedReason);

/// <summary>A freshly minted token. <see cref="Value"/> is readable exactly once.</summary>
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

public sealed record McpInfoDto(
    string ProtocolVersion,
    string ServerVersion,
    IReadOnlyList<McpToolInfoDto> Tools);

public sealed record McpToolInfoDto(string Name, string Title, string Description);
