using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

public sealed record UserDto(
    Guid Id,
    string Email,
    string? DisplayName,
    string? AvatarUrl,
    bool IsSuperAdmin);

/// <summary>Anonymous client bootstrap: where to send the user for OIDC sign-in.</summary>
public sealed record AuthConfigDto(
    string Authority,
    string ClientId,
    string Provider,
    bool SetupCompleted);

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

/// <summary>Opens a desktop browser sign-in flow (device-code style).</summary>
public sealed class DesktopAuthStartRequest
{
    /// <summary>Label for the session this flow will mint, e.g. "Solo Desktop (macOS)".</summary>
    [StringLength(200)]
    public string? Description { get; init; }
}

/// <summary>
/// A pending desktop sign-in. <see cref="State"/> rides in the browser URL and
/// only identifies the flow; <see cref="DeviceCode"/> never leaves the desktop
/// shell and is what redeems the session.
/// </summary>
public sealed record DesktopAuthFlowDto(
    string State,
    string DeviceCode,
    DateTimeOffset ExpiresAt);

public sealed class DesktopAuthCompleteRequest
{
    [Required]
    public required string State { get; init; }
}

public sealed class DesktopAuthPollRequest
{
    [Required]
    public required string DeviceCode { get; init; }
}

public sealed record InstanceSettingsDto(
    int AuditRetentionDays,
    bool SetupCompleted,
    uint Version);

public sealed class UpdateInstanceSettingsRequest
{
    [Range(1, 3650)]
    public int AuditRetentionDays { get; init; }

    public uint Version { get; init; }
}

public sealed record SetupStatusDto(bool SetupCompleted);

/// <summary>
/// Final setup-wizard step, sent by the SSO-authenticated user who completed the wizard.
/// The static credentials prove the caller is the operator performing first-run setup.
/// </summary>
public sealed class CompleteSetupRequest
{
    [Required]
    public required string SetupUsername { get; init; }

    [Required]
    public required string SetupPassword { get; init; }
}

public sealed class SetupSettingsRequest
{
    [Range(1, 3650)]
    public int AuditRetentionDays { get; init; }
}

public sealed record AuditEntryDto(
    long Id,
    DateTime Timestamp,
    Guid? UserId,
    string? UserDisplayName,
    string TableName,
    string RowId,
    string? ColumnName,
    string? PreviousValue);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>Identifies a stored image; the bytes are served from <c>GET /api/images/{id}</c>.</summary>
public sealed record ImageAssetDto(
    Guid Id,
    string ContentType);
