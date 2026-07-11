using Solo.Data.Entities;

namespace Solo.Api.Auth;

/// <summary>
/// Creates an <see cref="AuthSession"/> and the one-time-visible token behind
/// it — the single place the token/hash/lifetime rules live, shared by the
/// direct exchange (SessionsController) and the desktop browser flow.
/// </summary>
public static class AuthSessionMinter
{
    public static (AuthSession Session, string Token) Mint(
        Guid userId, string? description, IConfiguration config)
    {
        var token = SessionTokens.Generate();
        var session = new AuthSession
        {
            UserId = userId,
            TokenHash = SessionTokens.Hash(token),
            Description = description,
            ExpiresAt = DateTimeOffset.UtcNow + SessionAuthenticationHandler.SessionLifetime(config),
        };
        return (session, token);
    }
}
