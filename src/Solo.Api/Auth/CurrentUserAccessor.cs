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
}
