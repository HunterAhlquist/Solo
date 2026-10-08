namespace Solo.Shared.Dtos;

/// <summary>
/// A user as Admin settings lists them: who they are, whether they administer
/// the instance, and how many devices hold a live session for them.
/// </summary>
public sealed record AdminUserDto(
    Guid Id,
    string Email,
    string? DisplayName,
    string? AvatarUrl,
    bool IsSuperAdmin,
    int ActiveSessions);

public sealed class SetSuperAdminRequest
{
    public bool IsSuperAdmin { get; init; }
}

/// <summary>
/// What signing a user out everywhere revoked: core's sessions, and each
/// enabled extension's own credentials by extension id.
/// </summary>
public sealed record SignOutEverywhereResultDto(
    int Sessions,
    IReadOnlyList<RevokedCredentialsDto> Extensions);

public sealed record RevokedCredentialsDto(string ExtensionId, int Count);
