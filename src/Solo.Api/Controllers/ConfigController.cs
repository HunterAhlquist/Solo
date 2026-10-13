using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Data;
using Solo.Data.Entities;
using System.Reflection;
using Solo.Extensibility;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

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
        var settings = await db.InstanceSettings.AsNoTracking()
            .Where(s => s.Id == InstanceSettings.SingletonId)
            .Select(s => new { s.SetupCompleted, s.DisplayName, s.Tagline })
            .FirstOrDefaultAsync(ct);
        return new AuthConfigDto(
            config["Auth:Authority"] ?? string.Empty,
            config["Auth:ClientId"] ?? string.Empty,
            config["Auth:Provider"] ?? OidcClaimMapper.Generic,
            settings?.SetupCompleted ?? false)
        {
            DisplayName = settings?.DisplayName,
            Tagline = settings?.Tagline,
        };
    }

    /// <summary>
    /// What this instance is running, for the client's extension compat check.
    /// </summary>
    /// <remarks>
    /// Anonymous and on the bootstrap path because the client needs it before it
    /// decides which extension assemblies it may load. On the hosted web build
    /// the client and server ship together and this is a formality; in the
    /// Electron shell the client is bundled with the app and the API is
    /// proxied to whatever instance is configured, so the two genuinely differ.
    /// </remarks>
    [HttpGet("version")]
    [AllowAnonymous]
    public ActionResult<InstanceVersionDto> GetVersion() => new InstanceVersionDto(
        ContractVersion.Current,
        typeof(ConfigController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(ConfigController).Assembly.GetName().Version?.ToString()
            ?? "0.0.0");
}
