using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

public sealed class CreateSessionRequest
{
    /// <summary>Client label shown when listing sessions, e.g. "Solo Desktop (macOS)".</summary>
    [StringLength(200)]
    public string? Description { get; init; }
}

/// <summary>
/// A minted API session. <see cref="Token"/> is shown exactly once — the
/// server keeps only a hash.
/// </summary>
public sealed record SessionDto(
    string Token,
    DateTimeOffset ExpiresAt,
    UserDto User);

/// <summary>
/// One of the caller's sessions, as the signed-in devices list shows it. The
/// token itself is never readable again after it was minted.
/// </summary>
public sealed record AuthSessionDto(
    Guid Id,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrent);

/// <summary>Opens a PKCE-bound desktop browser sign-in flow.</summary>
public sealed class DesktopAuthStartRequest
{
    /// <summary>Label for the session this flow will mint, e.g. "Solo Desktop (macOS)".</summary>
    [StringLength(200)]
    public string? Description { get; init; }

    /// <summary>Base64url SHA-256 digest of the verifier held by the desktop process.</summary>
    [Required, StringLength(43, MinimumLength = 43)]
    [RegularExpression("^[A-Za-z0-9_-]+$")]
    public required string CodeChallenge { get; init; }

    /// <summary>Ephemeral IPv4 loopback port listening for the browser callback.</summary>
    [Range(1024, 65535)]
    public int LoopbackPort { get; init; }
}

/// <summary>
/// A pending desktop sign-in. <see cref="State"/> binds the browser callback to
/// the flow the desktop process opened.
/// </summary>
public sealed record DesktopAuthFlowDto(
    string State,
    DateTimeOffset ExpiresAt);

public sealed class DesktopAuthCompleteRequest
{
    [Required]
    public required string State { get; init; }
}

/// <summary>The one-time loopback destination produced after browser authentication.</summary>
public sealed record DesktopAuthCompletionDto(string CallbackUri);

public sealed class DesktopAuthRedeemRequest
{
    [Required, StringLength(32, MinimumLength = 32)]
    [RegularExpression("^[a-f0-9]+$")]
    public required string State { get; init; }

    [Required, StringLength(43, MinimumLength = 43)]
    [RegularExpression("^[A-Za-z0-9_-]+$")]
    public required string AuthorizationCode { get; init; }

    [Required, StringLength(128, MinimumLength = 43)]
    [RegularExpression("^[A-Za-z0-9._~-]+$")]
    public required string CodeVerifier { get; init; }
}
