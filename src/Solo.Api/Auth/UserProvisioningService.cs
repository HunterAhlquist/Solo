using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Data.Entities;

namespace Solo.Api.Auth;

/// <summary>
/// Finds or creates the <see cref="User"/> row for an SSO principal. Identity
/// is the OIDC iss + sub pair; the first valid sign-in provisions the row and
/// activates any pending project invites matching the token's email.
/// </summary>
public sealed class UserProvisioningService(SoloDbContext db, IConfiguration config)
{
    public async Task<User?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var provider = config["Auth:Provider"] ?? OidcClaimMapper.Generic;
        var identity = OidcClaimMapper.Map(principal, provider);
        if (identity is null)
        {
            return null;
        }

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Issuer == identity.Issuer && u.Subject == identity.Subject, ct);

        if (user is null)
        {
            user = new User
            {
                Issuer = identity.Issuer,
                Subject = identity.Subject,
                Email = identity.Email ?? string.Empty,
                DisplayName = identity.DisplayName,
                AvatarUrl = identity.AvatarUrl,
            };
            db.Users.Add(user);
        }
        else
        {
            // Keep profile fields fresh; these only dirty the row when changed.
            if (identity.Email is not null && user.Email != identity.Email)
            {
                user.Email = identity.Email;
            }

            if (identity.DisplayName is not null && user.DisplayName != identity.DisplayName)
            {
                user.DisplayName = identity.DisplayName;
            }

            if (identity.AvatarUrl is not null && user.AvatarUrl != identity.AvatarUrl)
            {
                user.AvatarUrl = identity.AvatarUrl;
            }
        }

        if (user.Email.Length > 0)
        {
            var pending = await db.ProjectMembers
                .Where(m => m.UserId == null && m.Email == user.Email)
                .ToListAsync(ct);
            foreach (var invite in pending)
            {
                invite.User = user;
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
        }

        return user;
    }
}
