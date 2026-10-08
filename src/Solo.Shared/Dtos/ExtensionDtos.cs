using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

/// <summary>
/// An extension the client should know about: its nav entries, and where to
/// fetch its client assembly.
/// </summary>
/// <param name="SoloClientVersion">Client contract range; the client checks this against its own build before loading.</param>
/// <param name="ClientAssembly">Assembly simple name to load, or null for a server-only extension.</param>
public sealed record ClientExtensionDto(
    string Id,
    string Name,
    string Version,
    string? SoloClientVersion,
    string? ClientAssembly,
    IReadOnlyList<ClientExtensionNavDto> Nav);

public sealed record ClientExtensionNavDto(
    string Route,
    string Label,
    string? Icon,
    string Requires);

/// <summary>Admin view of one registered extension, including ones whose package is gone.</summary>
/// <param name="Hooks">Operations this extension declares hooks on, for the "who is on this write path" view.</param>
/// <param name="CanPurge">True when the extension is not loaded this boot, which is what purge requires.</param>
public sealed record ExtensionRegistrationDto(
    string ExtensionId,
    string? DisplayName,
    string? Version,
    string? SchemaName,
    string State,
    string? FailureReason,
    DateTimeOffset InstalledAt,
    DateTimeOffset? LastLoadedAt,
    IReadOnlyList<string> Hooks,
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
