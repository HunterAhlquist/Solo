using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.Extensibility;

namespace Solo.DesignDocs.Controllers;

/// <summary>
/// A vault's images, stored in Postgres at their vault-relative paths and served
/// only to the project's members.
/// </summary>
/// <remarks>
/// <para>Unlike core's icons, these are members-only, so a plain
/// <c>&lt;img src&gt;</c> can't fetch them: the page downloads the bytes with
/// the sign-in credential and shows them from a <c>blob:</c> URL (contract 1.10
/// allows those in <c>img-src</c>).</para>
///
/// <para>The body is the raw image, re-encoded in the browser beforehand. The
/// bytes are sniffed and only PNG, JPEG, GIF and WebP are kept, whatever the
/// request claims: an SVG opened directly could run script.</para>
/// </remarks>
[Route("projects/{projectId:guid}/vaults/{vaultId:guid}/images")]
public sealed class ImagesController(DesignDocsDbContext db, ISoloQuery query, ISoloLiveUpdates live)
    : DocsControllerBase(db, live)
{
    /// <summary>
    /// The same string as core's <c>SecurityHeaders.IsolationPolicy</c>, which an
    /// extension can't reference: nothing in a served file can run or load anything.
    /// </summary>
    public const string IsolationPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";

    [HttpPost]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    [RequestSizeLimit(DocsLimits.ImageBytes)]
    public async Task<ActionResult<ImageSummaryDto>> Upload(
        Guid projectId, Guid vaultId, [FromQuery] string path, [FromQuery] int width, [FromQuery] int height,
        CancellationToken ct)
    {
        var (vault, problem) = await OpenVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return problem!;
        }

        if (DocNames.CleanPath(path, out var pathProblem) is not { } cleanPath)
        {
            return Invalid("Invalid path", pathProblem ?? "Name the image.");
        }

        var (bytes, contentType, readProblem) = await ReadImageAsync(ct);
        if (readProblem is not null)
        {
            return readProblem;
        }

        var image = new VaultImage
        {
            VaultId = vaultId,
            Path = cleanPath,
            ContentType = contentType!,
            Data = bytes!,
            Bytes = bytes!.Length,
            Width = Math.Clamp(width, 0, 100_000),
            Height = Math.Clamp(height, 0, 100_000),
            UploadedBy = (await query.CallerAsync(ct)).UserId,
        };
        Db.Images.Add(image);
        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Path taken",
                detail: "There's already an image at that path in this design doc.");
        }

        await TreeChangedAsync(projectId, vaultId, ct);
        return StatusCode(StatusCodes.Status201Created, image.ToSummary());
    }

    /// <summary>New bytes for the same image, so every embed of it updates.</summary>
    [HttpPut("{imageId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    [RequestSizeLimit(DocsLimits.ImageBytes)]
    public async Task<ActionResult<ImageSummaryDto>> Replace(
        Guid projectId, Guid vaultId, Guid imageId, [FromQuery] int width, [FromQuery] int height,
        CancellationToken ct)
    {
        var (vault, problem) = await OpenVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return problem!;
        }

        var image = await Db.Images.FirstOrDefaultAsync(i => i.Id == imageId && i.VaultId == vaultId, ct);
        if (image is null)
        {
            return NotFound();
        }

        var (bytes, contentType, readProblem) = await ReadImageAsync(ct);
        if (readProblem is not null)
        {
            return readProblem;
        }

        image.Data = bytes!;
        image.Bytes = bytes!.Length;
        image.ContentType = contentType!;
        image.Width = Math.Clamp(width, 0, 100_000);
        image.Height = Math.Clamp(height, 0, 100_000);
        image.UploadedBy = (await query.CallerAsync(ct)).UserId;
        image.UploadedAt = DateTimeOffset.UtcNow;
        await Db.SaveChangesAsync(ct);

        await TreeChangedAsync(projectId, vaultId, ct);
        await PublishAsync(projectId, DocsTopics.Vault(vaultId), DocsTopics.Image, ct, image.Id.ToString("N"));
        return image.ToSummary();
    }

    [HttpGet("{imageId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<IActionResult> Get(Guid projectId, Guid vaultId, Guid imageId, CancellationToken ct)
    {
        var image = await Db.Images.AsNoTracking()
            .Where(i => i.Id == imageId && i.VaultId == vaultId && Db.Vaults.Any(v => v.Id == vaultId && v.ProjectId == projectId))
            .Select(i => new { i.Data, i.ContentType })
            .FirstOrDefaultAsync(ct);
        if (image is null)
        {
            return NotFound();
        }

        // Members only, and replaceable in place, so never cached where another
        // person could be served it.
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = IsolationPolicy;
        return File(image.Data, image.ContentType);
    }

    private async Task<(byte[]? Bytes, string? ContentType, ObjectResult? Problem)> ReadImageAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            return (null, null, Invalid("No image", "Send the image's bytes as the request body."));
        }

        if (bytes.Length > DocsLimits.ImageBytes)
        {
            return (null, null, Invalid("Too large", $"Images can be at most {DocsLimits.ImageBytes / (1024 * 1024)} MB."));
        }

        return ImageSniffer.ContentTypeOf(bytes) is { } contentType
            ? (bytes, contentType, null)
            : (null, null, Invalid("Unsupported image", "Only PNG, JPEG, GIF and WebP images can be stored."));
    }
}

/// <summary>Tells an image's type from its first bytes, never from what the upload claims.</summary>
public static class ImageSniffer
{
    public static string? ContentTypeOf(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? "image/png"
        : bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? "image/jpeg"
        : bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) ? "image/gif"
        : bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;
}
