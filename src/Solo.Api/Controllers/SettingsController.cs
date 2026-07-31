using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Services;
using Solo.Api.Theming;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>Instance settings, editable by the super admin after setup.</summary>
[Route("api/settings")]
[Authorize(Policy = AuthConstants.SuperAdminPolicy)]
public sealed class SettingsController(SoloDbContext db, ThemeCatalog themes) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<InstanceSettingsDto>> Get(CancellationToken ct)
    {
        var settings = await db.InstanceSettings.AsNoTracking()
            .FirstAsync(s => s.Id == InstanceSettings.SingletonId, ct);
        return settings.ToDto();
    }

    [HttpPut]
    public async Task<ActionResult<InstanceSettingsDto>> Update(
        UpdateInstanceSettingsRequest request, CancellationToken ct)
    {
        // An id no theme answers to would leave the whole instance falling back
        // to the default with nothing to explain why, so refuse it here rather
        // than storing it and resolving it away on every read.
        if (request.ThemeId is { Length: > 0 } themeId
            && !themes.Themes.Any(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase)))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Unknown theme",
                detail: $"No theme with id '{themeId}' is installed on this instance.");
        }

        var settings = await db.InstanceSettings
            .FirstAsync(s => s.Id == InstanceSettings.SingletonId, ct);
        settings.AuditRetentionDays = request.AuditRetentionDays;
        settings.ThemeId = string.IsNullOrWhiteSpace(request.ThemeId) ? null : request.ThemeId;
        UseClientVersion(db, settings, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return settings.ToDto();
    }
}
