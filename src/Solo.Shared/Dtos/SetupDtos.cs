using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

public sealed record InstanceSettingsDto(
    int AuditRetentionDays,
    bool SetupCompleted,
    string? ThemeId,
    uint Version)
{
    /// <summary>The instance's own name, or null when it goes by "Solo".</summary>
    public string? DisplayName { get; init; }

    /// <summary>The instance's own sign-in tagline, or null when it shows the default.</summary>
    public string? Tagline { get; init; }
}

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

    /// <summary>
    /// The instance's display name. Empty clears it; null leaves it as it is.
    /// </summary>
    /// <remarks>
    /// Null cannot mean "clear" here the way it does for the theme. The
    /// desktop app bundles its own client, which can be older than the
    /// instance, and a client from before this field existed sends nothing for
    /// it — saving an unrelated setting there would otherwise wipe the name.
    /// </remarks>
    [StringLength(InstanceBranding.MaxDisplayNameLength)]
    public string? DisplayName { get; init; }

    /// <summary>
    /// The sign-in page's tagline, on the same terms as the name: empty clears
    /// it back to the default, null leaves it as it is.
    /// </summary>
    [StringLength(InstanceBranding.MaxTaglineLength)]
    public string? Tagline { get; init; }

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
