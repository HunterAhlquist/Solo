using Solo.Extensibility;
using Solo.Extensions;

namespace Solo.Api.Auth;

/// <summary>
/// Tells the extension host who the caller is and what project the request is
/// scoped to.
/// </summary>
/// <remarks>
/// The seam exists because Solo.Extensions cannot reference Solo.Api — the
/// dependency runs the other way — but the identity lives here, in
/// <see cref="CurrentUserAccessor"/>. The project id is whatever
/// <see cref="ProjectAccessHandler"/> already resolved and cached for this
/// request, so a hook is told the same project the authorization decision was
/// made against rather than a second, possibly different, resolution.
/// </remarks>
public sealed class ExtensionCallerAccessor(CurrentUserAccessor currentUser, SoloDbContextRoleLookup roles)
    : IExtensionCallerAccessor
{
    public async ValueTask<(ExtensionCaller Caller, Guid? ProjectId)> CurrentAsync(
        HttpContext http, CancellationToken ct)
    {
        var user = currentUser.User;
        var projectId = http.Items.TryGetValue(ProjectAccessHandler.ProjectIdItem, out var raw) && raw is Guid id
            ? id
            : (Guid?)null;

        if (user is null)
        {
            return (new ExtensionCaller(Guid.Empty, null, null, false, null, false), projectId);
        }

        var (role, isOwner) = projectId is null
            ? (null, false)
            : await roles.ForAsync(user.Id, projectId.Value, ct);

        return (
            new ExtensionCaller(user.Id, user.Email, user.DisplayName, currentUser.IsSuperAdmin, role, isOwner),
            projectId);
    }
}
