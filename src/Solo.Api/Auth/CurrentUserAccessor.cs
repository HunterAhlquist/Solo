using Solo.Data.Auditing;
using Solo.Data.Entities;

namespace Solo.Api.Auth;

/// <summary>
/// Scoped holder for the provisioned <see cref="Entities.User"/> of the current
/// request. Doubles as the audit interceptor's <see cref="ICurrentUserProvider"/>.
/// Null for anonymous requests, the static setup account, and background jobs.
/// </summary>
public sealed class CurrentUserAccessor : ICurrentUserProvider
{
    public User? User { get; set; }

    public Guid? UserId => User?.Id;

    /// <summary>
    /// True when the request is an extension's background work acting as
    /// <see cref="User"/> (contract 1.8), rather than the person themselves.
    /// </summary>
    public bool IsBackground { get; set; }

    /// <summary>
    /// Super admin as far as this request goes. Read this, not
    /// <c>User.IsSuperAdmin</c>, for any access decision: background work acts
    /// as a member and never as an admin, whoever granted it.
    /// </summary>
    public bool IsSuperAdmin => User?.IsSuperAdmin == true && !IsBackground;
}
