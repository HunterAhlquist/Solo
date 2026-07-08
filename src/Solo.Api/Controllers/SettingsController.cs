using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>Instance settings, editable by the super admin after setup.</summary>
[Route("api/settings")]
[Authorize(Policy = AuthConstants.SuperAdminPolicy)]
public sealed class SettingsController(SoloDbContext db) : ApiControllerBase
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
        var settings = await db.InstanceSettings
            .FirstAsync(s => s.Id == InstanceSettings.SingletonId, ct);
        settings.AuditRetentionDays = request.AuditRetentionDays;
        UseClientVersion(db, settings, request.Version);
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        return settings.ToDto();
    }
}
