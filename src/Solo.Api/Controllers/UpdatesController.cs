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

        // Stripped of line breaks before it is logged: it is the raw route
        // value, and nothing has yet checked it names an installed extension.
        var loggedId = extensionId.ReplaceLineEndings(string.Empty);
        logger.LogWarning(
            "Extension {Id} update requested by {User}.", loggedId, User.Identity?.Name);

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

        RestartAfterResponse($"extension {loggedId} {outcome.Version}");
        return new ExtensionUpdateResultDto(extensionId, outcome.Version!, Restarting: true);
    }

    /// <summary>
    /// Installs each named extension's update, then restarts once to load all
    /// of them.
    /// </summary>
    /// <remarks>
    /// <para>One restart rather than one per extension is the point: each
    /// restart disconnects everyone, and the single-extension endpoint above
    /// cannot be called again until the instance is back.</para>
    ///
    /// <para>A failure does not stop the rest, and does not stop the restart
    /// when anything else was installed. An installed package is already on
    /// disk in place of the old one, so holding the restart back would only
    /// leave it waiting for whichever restart comes next. Nothing is installed
    /// beyond the ids named, which are the updates the operator was shown and
    /// confirmed.</para>
    /// </remarks>
    [HttpPost("extensions")]
    public async Task<ActionResult<ExtensionBulkUpdateResultDto>> UpdateExtensions(
        UpdateExtensionsRequest request, CancellationToken ct)
    {
        var ids = request.ExtensionIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Nothing to update",
                detail: "Name at least one extension to update.");
        }

        logger.LogWarning(
            "Update of {Count} extensions requested by {User}.", ids.Count, User.Identity?.Name);

        var installed = new List<ExtensionUpdateResultDto>();
        var failed = new List<ExtensionUpdateFailureDto>();
        foreach (var id in ids)
        {
            var outcome = await installer.InstallAsync(id, ct);
            if (!outcome.Found)
            {
                failed.Add(new ExtensionUpdateFailureDto(id, "It is not installed on this instance."));
            }
            else if (outcome.Error is not null)
            {
                failed.Add(new ExtensionUpdateFailureDto(id, outcome.Error));
            }
            else
            {
                installed.Add(new ExtensionUpdateResultDto(id, outcome.Version!, Restarting: true));
            }
        }

        if (installed.Count > 0)
        {
            RestartAfterResponse($"{installed.Count} updated extensions");
        }

        return new ExtensionBulkUpdateResultDto(installed, failed, Restarting: installed.Count > 0);
    }

    /// <summary>
    /// Stops the host once the response has gone out. See
    /// <see cref="UpdateExtension"/> for why it waits for that.
    /// </summary>
    private void RestartAfterResponse(string loading) =>
        Response.OnCompleted(() =>
        {
            logger.LogWarning("Restarting to load {Loading}.", loading);
            lifetime.StopApplication();
            return Task.CompletedTask;
        });
}
