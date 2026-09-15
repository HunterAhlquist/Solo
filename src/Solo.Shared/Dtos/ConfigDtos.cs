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
