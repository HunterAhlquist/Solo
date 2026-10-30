namespace Solo.Shared.Dtos;

/// <summary>
/// What the instance knows about available updates. Super admin only: the
/// server update is an operator action (pull a new image) and the extension
/// updates write to the extensions volume and restart the instance, so nobody
/// else can act on any of it.
/// </summary>
/// <param name="Enabled">
/// False when update checks are switched off (<c>Updates__Enabled</c>). The UI
/// then says so rather than implying the instance is current.
/// </param>
/// <param name="CheckedAt">When the manifest behind this answer was fetched, if ever.</param>
/// <param name="Error">
/// Why the last check failed, if it did. Advisory: a failed check is reported,
/// never treated as "no update".
/// </param>
/// <param name="Server">The server's own update, when one is available.</param>
/// <param name="Extensions">
/// One entry per installed extension that has a newer version in a feed.
/// Extensions with no update, and ones with no feed to check, are absent.
/// </param>
public sealed record UpdateStatusDto(
    bool Enabled,
    DateTimeOffset? CheckedAt,
    string? Error,
    ServerUpdateDto? Server,
    IReadOnlyList<ExtensionUpdateDto> Extensions);

/// <param name="RunningVersion">This build, as <c>/api/config/version</c> reports it.</param>
/// <param name="AvailableVersion">The newer version the feed names.</param>
/// <param name="ReleaseUrl">Where to read about it and get it — the GitHub release.</param>
/// <param name="Image">
/// The image reference to pull, so the operator can copy it rather than
/// construct it.
/// </param>
/// <param name="Published">When that release was published.</param>
public sealed record ServerUpdateDto(
    string RunningVersion,
    string AvailableVersion,
    string ReleaseUrl,
    string? Image,
    DateTimeOffset? Published);

/// <param name="ExtensionId">The extension's id, matching its registration row.</param>
/// <param name="DisplayName">Its name, for a label that doesn't need the id.</param>
/// <param name="InstalledVersion">What is loaded right now.</param>
/// <param name="AvailableVersion">The newer version the feed names.</param>
/// <param name="SizeBytes">Download size, so the confirm dialog can state it.</param>
/// <param name="Compatible">
/// Whether this instance satisfies the new package's <c>soloApiVersion</c>
/// range. An incompatible update is shown but not offered: installing it would
/// produce a package the loader refuses at the next boot, which looks exactly
/// like the update having broken the extension.
/// </param>
/// <param name="Incompatibility">Why it would be refused, when it would be.</param>
public sealed record ExtensionUpdateDto(
    string ExtensionId,
    string? DisplayName,
    string? InstalledVersion,
    string AvailableVersion,
    long SizeBytes,
    bool Compatible,
    string? Incompatibility);

/// <summary>
/// Confirms an extension update. The id is retyped the way a purge retypes it,
/// because this one ends in a restart of the instance.
/// </summary>
public sealed class UpdateExtensionRequest
{
    public string ConfirmExtensionId { get; set; } = string.Empty;
}

/// <summary>
/// The outcome of staging an extension update.
/// </summary>
/// <param name="ExtensionId">The extension that was staged.</param>
/// <param name="Version">The version now on disk, which the next boot will load.</param>
/// <param name="Restarting">
/// Whether the instance is exiting to pick it up. False means the package is
/// staged but the restart is the operator's to perform.
/// </param>
public sealed record ExtensionUpdateResultDto(
    string ExtensionId,
    string Version,
    bool Restarting);
