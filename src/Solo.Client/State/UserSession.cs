using Solo.Client.Api;
using Solo.Shared.Dtos;

namespace Solo.Client.State;

/// <summary>
/// The signed-in user, hydrated once from <c>GET /api/users/me</c> after sign-in.
/// Its id drives presence self-exclusion; <c>IsSuperAdmin</c> gates the admin UI.
/// </summary>
public sealed class UserSession(ApiClient api)
{
    private Task<UserDto>? _load;

    public UserDto? Current { get; private set; }

    public Guid? UserId => Current?.Id;

    public bool IsSuperAdmin => Current?.IsSuperAdmin ?? false;

    /// <summary>Idempotent: concurrent callers share one in-flight request.</summary>
    public async Task<UserDto> LoadAsync(CancellationToken ct = default)
    {
        _load ??= api.GetCurrentUserAsync(ct);
        try
        {
            Current = await _load;
            return Current;
        }
        catch
        {
            // Don't cache a failure — a retry after re-auth should hit the API.
            _load = null;
            throw;
        }
    }

    /// <summary>Setup handoff grants super admin; refresh rather than force a reload.</summary>
    public void Replace(UserDto user)
    {
        Current = user;
        _load = Task.FromResult(user);
    }
}
