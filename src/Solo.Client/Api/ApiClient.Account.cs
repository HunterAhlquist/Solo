using Solo.Shared.Dtos;

namespace Solo.Client.Api;

public sealed partial class ApiClient
{
    // -- Signed-in devices ---------------------------------------------------
    /// <summary>The signed-in user's live sessions: one per desktop app holding one.</summary>
    public Task<IReadOnlyList<AuthSessionDto>> GetSessionsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AuthSessionDto>>("api/auth/sessions", ct);

    public Task RevokeSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/auth/sessions/{sessionId}", null, ct);

    /// <summary>Signs out every device but this one.</summary>
    public Task RevokeOtherSessionsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, "api/auth/sessions", null, ct);

    // -- Users (super admin) -------------------------------------------------
    public Task<IReadOnlyList<AdminUserDto>> GetAdminUsersAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AdminUserDto>>("api/admin/users", ct);

    /// <summary>Grants or removes super admin. Removing the last one is a 409.</summary>
    public Task<AdminUserDto> SetSuperAdminAsync(Guid userId, bool isSuperAdmin, CancellationToken ct = default) =>
        SendAsync<AdminUserDto>(HttpMethod.Put, $"api/admin/users/{userId}/super-admin",
            new SetSuperAdminRequest { IsSuperAdmin = isSuperAdmin }, ct);

    /// <summary>
    /// Revokes every session the user holds and every credential an extension
    /// issued them. A failing extension is a 500 naming it; sessions stay revoked.
    /// </summary>
    public Task<SignOutEverywhereResultDto> SignOutEverywhereAsync(Guid userId, CancellationToken ct = default) =>
        SendAsync<SignOutEverywhereResultDto>(HttpMethod.Post, $"api/admin/users/{userId}/sign-out-everywhere", null, ct);
}
