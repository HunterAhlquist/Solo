using Solo.Extensibility;
using Solo.Mcp.Data;

namespace Solo.Mcp.Auth;

/// <summary>
/// Lets core's "sign out everywhere" reach MCP tokens. An account holds at most
/// one live token, but revoking by identity rather than by id means a token
/// minted a moment ago is not missed.
/// </summary>
public sealed class McpCredentialIssuer(McpDbContext db) : ISoloCredentialIssuer
{
    public Task<int> RevokeAllAsync(CredentialHolder holder, CancellationToken ct) =>
        db.Tokens
            .Where(t => t.Issuer == holder.Issuer && t.Subject == holder.Subject)
            .RevokeAsync(McpTokenRevocation.RevokedByAdmin, ct);
}
