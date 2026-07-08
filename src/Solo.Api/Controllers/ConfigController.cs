using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

[Route("api/config")]
public sealed class ConfigController(IConfiguration config, SoloDbContext db) : ApiControllerBase
{
    /// <summary>
    /// Anonymous client bootstrap: the WASM app fetches the OIDC authority and
    /// client id here so the provider is configured in exactly one place (the
    /// API's environment).
    /// </summary>
    [HttpGet("auth")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthConfigDto>> GetAuthConfig(CancellationToken ct)
    {
        var setupCompleted = await db.InstanceSettings.AsNoTracking()
            .Where(s => s.Id == InstanceSettings.SingletonId)
            .Select(s => s.SetupCompleted)
            .FirstOrDefaultAsync(ct);
        return new AuthConfigDto(
            config["Auth:Authority"] ?? string.Empty,
            config["Auth:ClientId"] ?? string.Empty,
            config["Auth:Provider"] ?? OidcClaimMapper.Generic,
            setupCompleted);
    }
}
