using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Solo.Api.Auth;
using Solo.Api.Services;
using Solo.Data;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

public static class AvatarConstants
{
    public const string HttpClient = "avatar";
}

[Route("api/users")]
[Authorize]
public sealed class UsersController : ApiControllerBase
{
    /// <summary>
    /// The provisioned profile of the signed-in user; the client's first call after
    /// sign-in. A valid token alone is not enough: an account that can reach nothing —
    /// no project membership and not super admin — is refused, which is the client's
    /// cue to sign the user back out. Pending invites have already been activated by
    /// provisioning when this runs, so an invited user's first sign-in still passes.
    /// </summary>
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me(
        [FromServices] CurrentUserAccessor currentUser,
        [FromServices] SoloDbContext db,
        CancellationToken ct)
    {
        if (currentUser.User is not { } user)
        {
            return Forbid();
        }

        var hasAnyAccess = user.IsSuperAdmin ||
            await db.ProjectMembers.AsNoTracking().AnyAsync(m => m.UserId == user.Id, ct);
        return hasAnyAccess ? user.ToDto() : Forbid();
    }

    /// <summary>
    /// Proxies a user's SSO profile picture from our own origin. The client renders
    /// <c>&lt;img src="api/users/{id}/avatar"&gt;</c>, so the browser never touches the
    /// provider's CDN directly — which is what got it rate-limited (429) at board
    /// render volume. We fetch each picture once, cache the bytes, and serve them with
    /// a long cache header. Anonymous so a plain <c>&lt;img&gt;</c> resolves; the id is
    /// the only capability and a profile picture is not sensitive.
    /// </summary>
    [HttpGet("{id:guid}/avatar")]
    [AllowAnonymous]
    public async Task<IActionResult> Avatar(
        Guid id,
        [FromServices] SoloDbContext db,
        [FromServices] IHttpClientFactory httpClientFactory,
        [FromServices] IMemoryCache cache,
        CancellationToken ct)
    {
        var cacheKey = $"avatar:{id}";
        if (!cache.TryGetValue(cacheKey, out CachedAvatar? avatar))
        {
            avatar = await FetchAsync(id, db, httpClientFactory, ct);
            // Cache misses (no picture, or a failed fetch) too, briefly, so a broken
            // or missing avatar doesn't hammer the provider on every render.
            cache.Set(cacheKey, avatar,
                avatar is null ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(12));
        }

        if (avatar is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(avatar.Bytes, avatar.ContentType);
    }

    /// <summary>A profile picture that won't fit in 2 MB isn't one.</summary>
    private const int MaxAvatarBytes = 2 * 1024 * 1024;

    private static async Task<CachedAvatar?> FetchAsync(
        Guid id, SoloDbContext db, IHttpClientFactory httpClientFactory, CancellationToken ct)
    {
        var url = await db.Users.AsNoTracking()
            .Where(u => u.Id == id).Select(u => u.AvatarUrl).FirstOrDefaultAsync(ct);

        // The URL comes from the trusted IdP token's picture claim, but stay strict:
        // https only, and only serve it back if it really is an image.
        if (string.IsNullOrEmpty(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        // A picture claim pointing at the instance's own network is refused at
        // the socket, by the handler this client is built with (see Program.cs).
        try
        {
            var client = httpClientFactory.CreateClient(AvatarConstants.HttpClient);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Read through a bounded buffer rather than ReadAsByteArrayAsync:
            // the response is cached for 12 hours, so an oversized (or
            // endless) body would otherwise be pinned in memory.
            if (response.Content.Headers.ContentLength > MaxAvatarBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxAvatarBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.Length == 0 ? null : new CachedAvatar(buffer.ToArray(), contentType);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    private sealed record CachedAvatar(byte[] Bytes, string ContentType);
}
