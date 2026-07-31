using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Theming;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>
/// Serves the instance's theme and its content files.
/// </summary>
/// <remarks>
/// Reads are anonymous because the sign-in screen is themed too — its background
/// image has to load before anyone has signed in. What that exposes is the
/// instance's own branding, which every visitor sees anyway.
/// </remarks>
[Route("api/themes")]
public sealed class ThemesController(ThemeCatalog catalog, SoloDbContext db) : ApiControllerBase
{
    /// <summary>
    /// The active theme, fetched during the client's anonymous bootstrap so the
    /// first paint is already themed.
    /// </summary>
    [HttpGet("active")]
    [AllowAnonymous]
    public async Task<ActionResult<ThemeDto>> GetActive(CancellationToken ct)
    {
        var themeId = await db.InstanceSettings.AsNoTracking()
            .Where(s => s.Id == InstanceSettings.SingletonId)
            .Select(s => s.ThemeId)
            .FirstOrDefaultAsync(ct);

        // Deliberately not cached: switching the site theme should take effect on
        // the next load, and this is one small document on an already-required
        // bootstrap round trip. The content it points at is what gets cached.
        var package = catalog.Resolve(themeId);
        var dto = package.ToServedDto();

        // The instance's chosen theme has been removed from the themes
        // directory. The app renders fine on the default, but silently swapping
        // an operator's branding is not something to leave unsaid — the client
        // surfaces it (see ThemeState), and the catalog has already logged it.
        if (!string.IsNullOrWhiteSpace(themeId)
            && !string.Equals(package.Id, themeId, StringComparison.OrdinalIgnoreCase))
        {
            dto = dto with { IsFallback = true, RequestedThemeId = themeId };
        }

        return dto;
    }

    /// <summary>The installed themes, for the admin picker.</summary>
    [HttpGet]
    [Authorize(Policy = AuthConstants.SuperAdminPolicy)]
    public async Task<ActionResult<ThemeScanResultDto>> List(CancellationToken ct)
    {
        var themeId = await db.InstanceSettings.AsNoTracking()
            .Where(s => s.Id == InstanceSettings.SingletonId)
            .Select(s => s.ThemeId)
            .FirstOrDefaultAsync(ct);
        return catalog.ToScanResult(themeId);
    }

    /// <summary>
    /// Re-reads the themes volume, so dropping in a zip doesn't need a restart.
    /// </summary>
    [HttpPost("rescan")]
    [Authorize(Policy = AuthConstants.SuperAdminPolicy)]
    public async Task<ActionResult<ThemeScanResultDto>> Rescan(CancellationToken ct)
    {
        catalog.Rescan();
        return await List(ct);
    }

    /// <summary>
    /// A theme's content file. The hash segment is the package digest, so these
    /// URLs are immutable — a changed package serves a different URL rather than
    /// different bytes, and browsers (including the desktop shell, which proxies
    /// this route) can cache them indefinitely.
    /// </summary>
    [HttpGet("{themeId}/{hash}/{**path}")]
    [AllowAnonymous]
    public IActionResult GetContent(string themeId, string hash, string path)
    {
        if (!catalog.TryGetContent(themeId, hash, path, out var content))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        Response.Headers.XContentTypeOptions = "nosniff";

        // An SVG opened as a document — not through <img>, which never runs
        // script — is a document on this origin, and a theme's SVG is the one
        // theme file that could carry script. Same isolation ImagesController
        // applies to uploads: nothing loads, nothing to steal. Harmless to the
        // <img> and CSS url() paths themes actually use.
        if (content.ContentType == "image/svg+xml")
        {
            Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        }

        return File(content.Bytes, content.ContentType);
    }
}
