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

/// <summary>
/// What the instance is running, fetched anonymously at client bootstrap.
/// </summary>
/// <remarks>
/// Exists for the extension compat check. The hosted web build ships client and
/// server together so they can never disagree, but the Electron shell serves a
/// client bundled with the app and proxies the API to whatever instance is
/// configured — so the two halves version independently, and the client has to
/// be able to tell. Also lets the shell warn that it is older than the instance.
/// </remarks>
public sealed record InstanceVersionDto(
    /// <summary>The extension contract the server implements.</summary>
    string ExtensionApiVersion,
    /// <summary>Informational build version of the running server.</summary>
    string ServerVersion);

/// <summary>
/// An extension the client should know about: its nav entries, and where to
/// fetch its client assembly.
/// </summary>
public sealed record ClientExtensionDto(
    string Id,
    string Name,
    string Version,
    /// <summary>Client contract range; the client checks this against its own build before loading.</summary>
    string? SoloClientVersion,
    /// <summary>Assembly simple name to load, or null for a server-only extension.</summary>
    string? ClientAssembly,
    IReadOnlyList<ClientExtensionNavDto> Nav);

public sealed record ClientExtensionNavDto(
    string Route,
    string Label,
    string? Icon,
    string Requires);

/// <summary>Admin view of one registered extension, including ones whose package is gone.</summary>
public sealed record ExtensionRegistrationDto(
    string ExtensionId,
    string? DisplayName,
    string? Version,
    string? SchemaName,
    string State,
    string? FailureReason,
    DateTimeOffset InstalledAt,
    DateTimeOffset? LastLoadedAt,
    /// <summary>Operations this extension declares hooks on, for the "who is on this write path" view.</summary>
    IReadOnlyList<string> Hooks,
    /// <summary>True when the extension is not loaded this boot, which is what purge requires.</summary>
    bool CanPurge);

/// <summary>Package-level load failures, surfaced next to the registrations.</summary>
public sealed record ExtensionFailureDto(string Source, string Reason);

public sealed record ExtensionAdminDto(
    IReadOnlyList<ExtensionRegistrationDto> Extensions,
    IReadOnlyList<ExtensionFailureDto> Failures);

/// <summary>Purge confirmation: the caller retypes the extension id.</summary>
public sealed class PurgeExtensionRequest
{
    [Required]
    public required string ConfirmExtensionId { get; init; }
}

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

public sealed record InstanceSettingsDto(
    int AuditRetentionDays,
    bool SetupCompleted,
    string? ThemeId,
    uint Version);

public sealed class UpdateInstanceSettingsRequest
{
    [Range(1, 3650)]
    public int AuditRetentionDays { get; init; }

    /// <summary>
    /// Site-wide theme, applied to every user. Null selects the built-in
    /// default, which is also the fallback when the named theme is missing.
    /// </summary>
    [StringLength(64)]
    public string? ThemeId { get; init; }

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
