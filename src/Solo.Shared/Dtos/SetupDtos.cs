using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

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
