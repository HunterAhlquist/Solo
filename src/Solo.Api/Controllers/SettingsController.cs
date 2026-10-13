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

        // The name is drawn in page titles and native menus, where a line
        // break or other control character has no good outcome; the tagline is
        // one line on the sign-in card.
        var displayName = request.DisplayName?.Trim();
        var tagline = request.Tagline?.Trim();
        if (new[] { displayName, tagline }.Any(text => text is not null && text.Any(char.IsControl)))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid branding",
                detail: "The display name and tagline can't contain control characters.");
        }

        var settings = await db.InstanceSettings
            .FirstAsync(s => s.Id == InstanceSettings.SingletonId, ct);
        settings.AuditRetentionDays = request.AuditRetentionDays;
        settings.ThemeId = string.IsNullOrWhiteSpace(request.ThemeId) ? null : request.ThemeId;
        // Null is "not sent", which leaves each alone; see the request.
        if (displayName is not null)
        {
            settings.DisplayName = displayName.Length == 0 ? null : displayName;
        }
        if (tagline is not null)
        {
            settings.Tagline = tagline.Length == 0 ? null : tagline;
        }
        UseClientVersion(db, settings, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return settings.ToDto();
    }
}
