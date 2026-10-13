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
    bool SetupCompleted)
{
    /// <summary>
    /// What the instance calls itself, or null when it has no name of its own
    /// and goes by "Solo". Here rather than behind sign-in because the sign-in
    /// page is the first thing to show it, and because the desktop app reads
    /// it from this same call to label the instance in its menus.
    /// </summary>
    /// <remarks>
    /// A property rather than a constructor parameter so the record's
    /// constructor keeps its shape for anything already compiled against it.
    /// </remarks>
    public string? DisplayName { get; init; }

    /// <summary>
    /// The line under the name on the sign-in page, or null when the instance
    /// has none of its own and shows the product's.
    /// </summary>
    public string? Tagline { get; init; }
}

/// <summary>
/// The rules both ends apply to what an instance calls itself, and what it
/// shows where its operator has said nothing.
/// </summary>
public static class InstanceBranding
{
    /// <summary>The product's own name, which an unnamed instance goes by.</summary>
    public const string ProductName = "Solo";

    /// <summary>The sign-in page's line for an instance without one of its own.</summary>
    public const string DefaultTagline = "Project management for game development.";

    /// <summary>
    /// Short enough for an app bar on a phone and a menu item on a desktop.
    /// </summary>
    public const int MaxDisplayNameLength = 40;

    /// <summary>A sentence, which is what fits under the name on the sign-in card.</summary>
    public const int MaxTaglineLength = 120;
}

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
/// <param name="ExtensionApiVersion">The extension contract the server implements.</param>
/// <param name="ServerVersion">Informational build version of the running server.</param>
public sealed record InstanceVersionDto(
    string ExtensionApiVersion,
    string ServerVersion);
