using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>
/// Stores small custom icons (project/board/label images) inline in Postgres.
/// Uploads require a signed-in user; reads are anonymous so a plain
/// <c>&lt;img src="api/images/{id}"&gt;</c> resolves without an auth header — the
/// GUID id is the only capability, and the bytes are immutable per id so they
/// cache forever.
/// </summary>
[Route("api/images")]
public sealed class ImagesController(SoloDbContext db) : ApiControllerBase
{
    /// <summary>1 MB — these are icons, not photos.</summary>
    private const long MaxBytes = 1024 * 1024;

    private static readonly Dictionary<string, string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/svg+xml"] = ".svg",
    };

    [HttpPost]
    [Authorize]
    [RequestSizeLimit(MaxBytes + 4096)]
    public async Task<ActionResult<ImageAssetDto>> Upload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "No file",
                detail: "Attach an image file in the 'file' field.");
        }

        if (file.Length > MaxBytes)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Too large",
                detail: $"Images must be {MaxBytes / 1024} KB or smaller.");
        }

        if (!AllowedContentTypes.ContainsKey(file.ContentType))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Unsupported type",
                detail: "Only PNG, JPG and SVG images are allowed.");
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);

        var image = new ImageAsset
        {
            ContentType = file.ContentType,
            Data = buffer.ToArray(),
        };
        db.ImageAssets.Add(image);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = image.Id }, image.ToDto());
    }

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var image = await db.ImageAssets.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (image is null)
        {
            return NotFound();
        }

        // Immutable: the content under an id never changes, so cache aggressively.
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(image.Data, image.ContentType);
    }
}
