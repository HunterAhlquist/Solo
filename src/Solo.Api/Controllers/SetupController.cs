using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>
/// First-run setup wizard. A fresh deployment starts in setup mode with a
/// static super admin account (credentials from environment config). The final
/// step hands super admin off to the SSO user who completed the wizard and
/// permanently disables the static account.
/// </summary>
[Route("api/setup")]
public sealed class SetupController(SoloDbContext db, IConfiguration config) : ApiControllerBase
{
    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<ActionResult<SetupStatusDto>> GetStatus(CancellationToken ct)
    {
        var completed = await db.InstanceSettings.AsNoTracking()
            .Where(s => s.Id == InstanceSettings.SingletonId)
            .Select(s => s.SetupCompleted)
            .FirstOrDefaultAsync(ct);
        return new SetupStatusDto(completed);
    }

    /// <summary>Wizard step: confirm the static setup credentials work.</summary>
    [HttpPost("verify")]
    [Authorize(Policy = AuthConstants.SetupPolicy, AuthenticationSchemes = AuthConstants.SetupScheme)]
    public IActionResult Verify() => NoContent();

    /// <summary>Wizard step: initial instance configuration via the static account.</summary>
    [HttpPut("settings")]
    [Authorize(Policy = AuthConstants.SetupPolicy, AuthenticationSchemes = AuthConstants.SetupScheme)]
    public async Task<ActionResult<InstanceSettingsDto>> UpdateSettings(
        SetupSettingsRequest request, CancellationToken ct)
    {
        var settings = await db.InstanceSettings
            .FirstAsync(s => s.Id == InstanceSettings.SingletonId, ct);
        settings.AuditRetentionDays = request.AuditRetentionDays;
        await db.SaveChangesAsync(ct);
        return settings.ToDto();
    }

    /// <summary>
    /// Final wizard step, called by the SSO-signed-in user who completed the
    /// steps. The static credentials in the body prove the caller is the
    /// operator. Grants that user super admin, flips SetupCompleted, and the
    /// static account is dead from then on.
    /// </summary>
    [HttpPost("complete")]
    [Authorize]
    public async Task<ActionResult<UserDto>> Complete(
        CompleteSetupRequest request,
        [FromServices] CurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (currentUser.User is null)
        {
            return Forbid();
        }

        if (!SetupAuthenticationHandler.CredentialsValid(
                request.SetupUsername, request.SetupPassword, config))
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Invalid setup credentials");
        }

        var settings = await db.InstanceSettings
            .FirstAsync(s => s.Id == InstanceSettings.SingletonId, ct);
        if (settings.SetupCompleted)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Setup already completed");
        }

        // The user entity is tracked by this request's scoped context.
        currentUser.User.IsSuperAdmin = true;
        settings.SetupCompleted = true;
        await db.SaveChangesAsync(ct);
        return currentUser.User.ToDto();
    }
}
