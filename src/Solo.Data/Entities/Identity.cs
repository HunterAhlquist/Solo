using Solo.Shared;

namespace Solo.Data.Entities;

/// <summary>Provisioned on first SSO sign-in; identity is the OIDC iss + sub pair.</summary>
public class User : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public bool IsSuperAdmin { get; set; }
    public uint Version { get; set; }
}

/// <summary>
/// A long-lived API session minted in exchange for a fresh OIDC ID token, so
/// clients that cannot renew provider tokens silently (the desktop app) stay
/// signed in. Stores only a hash — the token itself is shown once and kept by
/// the client. Expiry slides on use; an idle session eventually dies.
/// </summary>
public class AuthSession
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    /// <summary>SHA-256 of the opaque bearer token, hex-encoded.</summary>
    public required string TokenHash { get; set; }
    /// <summary>Client-supplied label, e.g. "Solo Desktop (macOS)".</summary>
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}
