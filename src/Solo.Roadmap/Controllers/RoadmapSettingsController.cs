using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Extensibility;
using Solo.Roadmap.Contracts;
using Solo.Roadmap.Data;

namespace Solo.Roadmap.Controllers;

/// <summary>
/// The timeline's instance-wide settings: how far it scrolls each way, and the
/// stripe cadence. Everyone signed in reads them; only a super admin changes them.
/// </summary>
/// <remarks>
/// No project in the route, so the project policies can't apply: the read is a
/// plain <c>[Authorize]</c> — the settings say nothing about any project — and
/// the write is guarded by the contract's super-admin policy, not by the fact
/// that only Admin settings links here.
/// </remarks>
[ApiController]
[Route("settings")]
public sealed class RoadmapSettingsController(RoadmapDbContext db, RoadmapSettingsStore store) : ControllerBase
{
    [HttpGet]
    [Authorize]
    public async Task<RoadmapSettingsDto> Get(CancellationToken ct) => (await store.GetAsync(ct)).ToDto();

    [HttpPut]
    [Authorize(Policy = SoloPolicies.SuperAdmin)]
    public async Task<ActionResult<RoadmapSettingsDto>> Update(
        UpdateRoadmapSettingsRequest request, CancellationToken ct)
    {
        // The day of year becomes a date in the year it's saved, and it's the date
        // that's stored — so the stripes keep their cadence across New Year.
        var year = store.CurrentYear;
        if (RoadmapSettingsStore.ResolveAnchor(request.StripeStartDayOfYear, year) is not { } anchor)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "No such day this year",
                detail: $"{year} has {(DateTime.IsLeapYear(year) ? 366 : 365)} days.");
        }

        var settings = await store.GetAsync(ct);
        settings.PastDays = request.PastDays;
        settings.FutureDays = request.FutureDays;
        settings.StripeAnchor = anchor;
        settings.StripeIntervalDays = request.StripeIntervalDays;
        db.Entry(settings).Property(s => s.Version).OriginalValue = request.Version;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Concurrency conflict",
                detail: "The settings were changed by someone else. Reload them and try again.");
        }

        return settings.ToDto();
    }
}
