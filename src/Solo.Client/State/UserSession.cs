using System.Net;
using Solo.Client.Api;
using Solo.Shared;
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

    /// <summary>
    /// What this user may do in a project, from its membership list. Kept here
    /// because both halves mirror a server rule and drift if they are restated:
    /// the API's write policy accepts the owner flag or a read/write role and
    /// nothing else, while the super admin's blanket rights reach the owner
    /// surface — audit, settings, member and comment moderation — but never
    /// write access it was not granted by membership.
    /// </summary>
    public (bool IsOwner, bool CanWrite) AccessIn(IEnumerable<ProjectMemberDto> members)
    {
        var me = members.FirstOrDefault(m => m.UserId == UserId);
        var isOwner = me?.IsOwner ?? false;
        return (IsSuperAdmin || isOwner, isOwner || me?.Role == ProjectRole.ReadWrite);
    }

    /// <summary>
    /// The API refused the profile outright (403): signed in, but allowed nothing.
    /// Unlike <see cref="Auth.SessionExpiry"/> there is nothing to renew — the layout
    /// listens and signs the user back out.
    /// </summary>
    public event Action? AccessDenied;

    /// <summary>Idempotent: concurrent callers share one in-flight request.</summary>
    public async Task<UserDto> LoadAsync(CancellationToken ct = default)
    {
        _load ??= api.GetCurrentUserAsync(ct);
        try
        {
            Current = await _load;
            return Current;
        }
        catch (Exception e)
        {
            // Don't cache a failure — a retry after re-auth should hit the API.
            _load = null;
            if (e is ApiException { StatusCode: HttpStatusCode.Forbidden })
            {
                AccessDenied?.Invoke();
            }

            throw;
        }
    }

    /// <summary>Setup handoff grants super admin; refresh rather than force a reload.</summary>
    public void Replace(UserDto user)
    {
        Current = user;
        _load = Task.FromResult(user);
    }

    /// <summary>
    /// Drops the cached profile on sign-out, so signing in as a different account
    /// re-hydrates from <c>/api/users/me</c> instead of reusing the memoized load.
    /// </summary>
    public void Clear()
    {
        Current = null;
        _load = null;
    }
}
