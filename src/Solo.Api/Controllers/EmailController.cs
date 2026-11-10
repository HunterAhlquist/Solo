using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Email;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared;

namespace Solo.Api.Controllers;

[Route("api/email")]
public sealed class EmailController(SoloDbContext db, DigestService digests) : ApiControllerBase
{
    /// <summary>
    /// RFC 8058 one-click unsubscribe, which mail apps POST to from the digest's
    /// List-Unsubscribe header without anyone signed in. The token is the only
    /// credential, and all it can do is turn that person's digest off.
    /// </summary>
    [HttpPost("unsubscribe")]
    [AllowAnonymous]
    public async Task<IActionResult> Unsubscribe([FromQuery] string token, CancellationToken ct)
    {
        if (digests.UserFromUnsubscribeToken(token) is not { } userId || !await db.Users.AnyAsync(u => u.Id == userId, ct))
        {
            return BadRequest();
        }

        var preferences = await db.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (preferences is null)
        {
            preferences = new NotificationPreferences { UserId = userId };
            db.NotificationPreferences.Add(preferences);
        }

        preferences.DigestFrequency = DigestFrequency.Off;
        await db.SaveChangesAsync(ct);
        return Ok("Your email digest is off.");
    }
}
