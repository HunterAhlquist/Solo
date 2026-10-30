using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solo.Api.Auth;
using Solo.Api.Updates;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>
/// What is out of date, and the one update this instance can apply to itself.
/// </summary>
/// <remarks>
/// <para>Super admin throughout. The server update is an operator action — pull
/// a new image — and an extension update writes to the extensions volume and
/// restarts the instance. Nobody else can act on any of it, and the inventory
/// alone names every extension an instance runs and the exact build of the
/// server, which is reconnaissance worth keeping behind the same door as the
/// rest of Admin settings.</para>
///
/// <para>The client reads it from here rather than from GitHub because it
/// cannot do otherwise: the CSP is <c>connect-src 'self'</c> plus the identity
/// provider (<see cref="SecurityHeaders"/>).</para>
/// </remarks>
[Route("api/updates")]
[Authorize(Policy = AuthConstants.SuperAdminPolicy)]
public sealed class UpdatesController(
    UpdateChecker checker,
    ExtensionUpdateInstaller installer,
    IHostApplicationLifetime lifetime,
    ILogger<UpdatesController> logger) : ApiControllerBase
{
    /// <summary>
    /// Server and extension updates. Served from a cached manifest; pass
    /// <c>refresh=true</c> to re-read the feeds.
    /// </summary>
    /// <remarks>
    /// Never fails because a feed did. An unreachable feed comes back as
    /// <see cref="UpdateStatusDto.Error"/> with no updates listed, so the page
    /// can say "couldn't check" instead of "up to date" — the distinction being
    /// the entire value of the indicator.
    /// </remarks>
    [HttpGet]
    public async Task<ActionResult<UpdateStatusDto>> Get(
        [FromQuery] bool refresh, CancellationToken ct) =>
        await checker.GetStatusAsync(refresh, ct);

    /// <summary>
    /// Downloads an extension update, verifies it, puts it in the extensions
    /// directory and restarts the instance to load it.
    /// </summary>
    /// <remarks>
    /// <para><b>This ends in the process exiting.</b> Extensions register
    /// controllers, hooks and a DbContext before the host is built, so there is
    /// no rescan and a new package can only take effect at a boot. The instance
    /// comes back because something restarts it — <c>restart: unless-stopped</c>
    /// in the compose file, or the equivalent in whatever runs the container. On
    /// a host with no restart policy the instance stays down until it is started
    /// by hand, which is why the client says so before asking for
    /// confirmation.</para>
    ///
    /// <para>The exit is hung off <see cref="HttpResponse.OnCompleted"/>: the
    /// caller needs the 200 telling it to expect a restart, and stopping the
    /// host while the response is still buffered would deny it that and leave
    /// the UI unable to distinguish a restart from a crash.</para>
    /// </remarks>
    [HttpPost("extensions/{extensionId}")]
    public async Task<ActionResult<ExtensionUpdateResultDto>> UpdateExtension(
        string extensionId, UpdateExtensionRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.ConfirmExtensionId, extensionId, StringComparison.Ordinal))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Confirmation does not match",
                detail: "Type the extension's id exactly to confirm the update.");
        }

        logger.LogWarning(
            "Extension {Id} update requested by {User}.", extensionId, User.Identity?.Name);

        var outcome = await installer.InstallAsync(extensionId, ct);
        if (!outcome.Found)
        {
            return NotFound();
        }

        if (outcome.Error is not null)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Update not applied",
                detail: outcome.Error);
        }

        Response.OnCompleted(() =>
        {
            logger.LogWarning(
                "Restarting to load extension {Id} {Version}.", extensionId, outcome.Version);
            lifetime.StopApplication();
            return Task.CompletedTask;
        });

        return new ExtensionUpdateResultDto(extensionId, outcome.Version!, Restarting: true);
    }
}
