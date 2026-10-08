using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Realtime;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Extensibility;
using Solo.Extensions;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>
/// The users of the instance, for the super admin: who administers it, and a
/// way to sign someone out everywhere.
/// </summary>
/// <remarks>
/// <para>Before this existed, super admin was granted once by the setup wizard
/// and could never be removed, and the only session anyone could revoke was the
/// one they were holding — so a leaked super-admin credential was a takeover
/// with no remedy short of SQL.</para>
///
/// <para>Each change is logged at warning, like an extension purge: the audit
/// interceptor records project-scoped rows, and a user is not one.</para>
/// </remarks>
[Route("api/admin/users")]
[Authorize(Policy = AuthConstants.SuperAdminPolicy)]
public sealed class AdminUsersController(
    SoloDbContext db,
    CurrentUserAccessor currentUser,
    LiveAccess live,
    ILogger<AdminUsersController> logger) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminUserDto>>> List(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.Users.AsNoTracking()
            .OrderBy(u => u.DisplayName ?? u.Email)
            .Select(u => new AdminUserDto(
                u.Id, u.Email, u.DisplayName, u.AvatarUrl, u.IsSuperAdmin,
                db.AuthSessions.Count(s => s.UserId == u.Id && s.ExpiresAt > now)))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Grants or removes super admin. The last one can't be removed: nobody
    /// would be left to grant it back.
    /// </summary>
    [HttpPut("{userId:guid}/super-admin")]
    public async Task<ActionResult<AdminUserDto>> SetSuperAdmin(
        Guid userId, SetSuperAdminRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Lock every super admin row before counting. Without it, two admins
        // removing each other at the same moment would each see the other
        // still standing, and both removals would commit.
        // Uncomposed, so EF sends the locking clause as written.
        var admins = await db.Database
            .SqlQueryRaw<Guid>("""SELECT "Id" AS "Value" FROM "Users" WHERE "IsSuperAdmin" FOR UPDATE""")
            .ToListAsync(ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return NotFound();
        }

        if (!request.IsSuperAdmin && user.IsSuperAdmin && admins.Count <= 1)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Last super admin",
                detail: "Make someone else a super admin before removing the last one.");
        }

        if (user.IsSuperAdmin != request.IsSuperAdmin)
        {
            user.IsSuperAdmin = request.IsSuperAdmin;
            await db.SaveChangesAsync(ct);
            logger.LogWarning(
                "{Change} super admin for {Email} ({UserId}) at the request of {Actor}.",
                request.IsSuperAdmin ? "Granted" : "Removed",
                user.Email, user.Id, currentUser.User!.Email);
        }

        await transaction.CommitAsync(ct);
        return await ToDtoAsync(user, ct);
    }

    /// <summary>
    /// Signs the user out everywhere: revokes the sessions core issued them,
    /// then every credential an enabled extension issued them, such as an MCP
    /// token.
    /// </summary>
    /// <remarks>
    /// <para>Sessions go first and stay revoked whatever an extension does
    /// next. An extension that fails is named in the answer rather than
    /// swallowed: an admin cutting off a leaked credential has to know if one
    /// survived.</para>
    ///
    /// <para>A browser signed in with the provider holds nothing to revoke here;
    /// its ID token lapses within the hour.</para>
    /// </remarks>
    [HttpPost("{userId:guid}/sign-out-everywhere")]
    public async Task<ActionResult<SignOutEverywhereResultDto>> SignOutEverywhere(
        Guid userId, [FromServices] ExtensionCredentialIssuers issuers, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return NotFound();
        }

        var sessionIds = await db.AuthSessions.Where(s => s.UserId == userId).Select(s => s.Id).ToListAsync(ct);
        var sessions = await db.AuthSessions.Where(s => sessionIds.Contains(s.Id)).ExecuteDeleteAsync(ct);
        live.EndSessions(sessionIds);

        var holder = new CredentialHolder(user.Id, user.Issuer, user.Subject);
        var revoked = new List<RevokedCredentialsDto>();
        var failed = new List<string>();
        foreach (var (extensionId, issuer) in issuers.Enabled())
        {
            try
            {
                revoked.Add(new RevokedCredentialsDto(extensionId, await issuer.RevokeAllAsync(holder, ct)));
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogError(e, "Extension {ExtensionId} could not revoke the credentials of {UserId}.",
                    extensionId, user.Id);
                failed.Add(extensionId);
            }
        }

        logger.LogWarning(
            "Signed {Email} ({UserId}) out everywhere at the request of {Actor}: {Sessions} session(s), {Extensions}.",
            user.Email, user.Id, currentUser.User!.Email, sessions,
            string.Join(", ", revoked.Select(r => $"{r.ExtensionId} {r.Count}")));

        if (failed.Count > 0)
        {
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Some credentials may still work",
                detail: $"Their sessions were revoked, but {string.Join(", ", failed)} couldn't revoke its own "
                        + "credentials. Try again, or revoke them from that extension's page.");
        }

        return new SignOutEverywhereResultDto(sessions, revoked);
    }

    private async Task<AdminUserDto> ToDtoAsync(User user, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.AuthSessions.CountAsync(s => s.UserId == user.Id && s.ExpiresAt > now, ct);
        return new AdminUserDto(
            user.Id, user.Email, user.DisplayName, user.AvatarUrl, user.IsSuperAdmin, sessions);
    }
}
