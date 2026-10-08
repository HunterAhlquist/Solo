using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>
/// Stores small custom icons (project/board/label images) inline in Postgres.
/// Uploads need write access to a project; reads are anonymous so a plain
/// <c>&lt;img src="api/images/{id}"&gt;</c> resolves without an auth header — the
/// GUID id is the only capability, and the bytes are immutable per id so they
/// cache forever.
/// </summary>
[Route("api/images")]
public sealed class ImagesController(SoloDbContext db, CurrentUserAccessor currentUser) : ApiControllerBase
{
    /// <summary>1 MB — these are icons, not photos.</summary>
    private const long MaxBytes = 1024 * 1024;

    private static readonly Dictionary<string, string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/svg+xml"] = ".svg",
    };

    /// <summary>
    /// Uploads are served from the app's own origin, and an SVG opened as a
    /// document — not through <c>&lt;img&gt;</c>, which never runs script — is a
    /// document on that origin, so a scripted SVG would be stored XSS against
    /// whoever follows the link, with the session token one localStorage read
    /// away. Any authenticated member can upload one. This is what stops it:
    /// <c>default-src 'none'</c> blocks every subresource and script the file
    /// might reach for, and <c>sandbox</c> drops the response into an opaque
    /// origin so even inline script has nothing to steal. Neither affects the
    /// <c>&lt;img&gt;</c> path the app actually uses.
    /// </summary>
    private const string IsolationPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";

    [HttpPost]
    [Authorize]
    [RequestSizeLimit(MaxBytes + 4096)]
    public async Task<ActionResult<ImageAssetDto>> Upload(IFormFile file, CancellationToken ct)
    {
        if (!await CanPlaceAnIconAsync(ct))
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Not allowed",
                detail: "Uploading an image needs write access to a project.");
        }

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
        var bytes = buffer.ToArray();

        // The declared content type is the uploader's word for it, and it is
        // what every later response echoes back. Check the bytes agree, so a
        // file cannot be stored under a type it isn't.
        if (!LooksLike(file.ContentType, bytes))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Not an image",
                detail: "The file contents don't match the declared image type.");
        }

        var image = new ImageAsset
        {
            ContentType = file.ContentType,
            Data = bytes,
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
        Response.Headers.ContentSecurityPolicy = IsolationPolicy;
        return File(image.Data, image.ContentType);
    }

    /// <summary>
    /// Every image is an icon for a project, board or label, so only someone who
    /// could put one there may upload: write access somewhere (the same test as
    /// <c>AccessPolicy</c>'s Write level), or super admin, who creates projects.
    /// Nothing deletes an image, and the bytes are served to anyone holding the
    /// id, so a read-only member has no business adding rows.
    /// </summary>
    private async Task<bool> CanPlaceAnIconAsync(CancellationToken ct)
    {
        var user = currentUser.User!;
        return user.IsSuperAdmin || await db.ProjectMembers.AsNoTracking()
            .AnyAsync(m => m.UserId == user.Id && (m.IsOwner || m.Role == ProjectRole.ReadWrite), ct);
    }

    /// <summary>
    /// Cheap sniff that the bytes are the type they claim. PNG and JPEG have
    /// fixed signatures; SVG is XML, so require it to open like one.
    /// </summary>
    private static bool LooksLike(string contentType, ReadOnlySpan<byte> bytes) =>
        contentType.ToLowerInvariant() switch
        {
            "image/png" => bytes.StartsWith(PngSignature),
            "image/jpeg" => bytes.StartsWith(JpegSignature),
            "image/svg+xml" => IsXml(bytes),
            _ => false,
        };

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static bool IsXml(ReadOnlySpan<byte> bytes)
    {
        // Skip a UTF-8 BOM and any leading whitespace before the first tag.
        if (bytes.StartsWith(Utf8Bom))
        {
            bytes = bytes[3..];
        }

        var start = 0;
        while (start < bytes.Length && char.IsWhiteSpace((char)bytes[start]))
        {
            start++;
        }

        return start < bytes.Length && bytes[start] == (byte)'<';
    }
}
