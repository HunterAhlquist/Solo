namespace Solo.Data.Auditing;

/// <summary>
/// Supplies the acting user to the audit interceptor. Populated per request by
/// auth middleware in the API host; null for system actions (e.g. the static
/// setup account or background jobs).
/// </summary>
public interface ICurrentUserProvider
{
    Guid? UserId { get; }
}
