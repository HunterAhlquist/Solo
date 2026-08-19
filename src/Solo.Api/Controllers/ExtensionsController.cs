using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solo.Api.Auth;
using Solo.Data.Entities;
using Solo.Extensibility;
using Solo.Extensions;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>
/// Serves extension client assemblies and content, and the admin surface for
/// listing and uninstalling them.
/// </summary>
[Route("api/extensions")]
public sealed class ExtensionsController(
    ExtensionState state,
    ExtensionRegistry registry,
    ILogger<ExtensionsController> logger) : ApiControllerBase
{
    /// <summary>
    /// What the client needs to boot extensions: nav entries and which assembly
    /// to fetch. Only enabled extensions appear — an extension disabled by a
    /// failed migration must not contribute a nav entry pointing at a route its
    /// assembly will never register.
    /// </summary>
    /// <remarks>
    /// Anonymous, and it has to be: the client fetches this during bootstrap,
    /// before the router exists and long before anyone has signed in. The same
    /// goes for the assembly and content endpoints below — an extension page has
    /// to be routable on the sign-in screen, exactly as the active theme has to
    /// be loadable there.
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    public ActionResult<IReadOnlyList<ClientExtensionDto>> List() =>
        state.Enabled
            .Where(p => p.ClientAssembly is not null)
            .Select(p => new ClientExtensionDto(
                p.Id,
                p.Manifest.Name,
                p.Manifest.Version,
                p.Manifest.SoloClientVersion,
                Path.GetFileNameWithoutExtension(p.Manifest.Client),
                p.Manifest.Nav
                    .Select(n => new ClientExtensionNavDto(n.Route, n.Label, n.Icon, n.Requires.ToString()))
                    .ToList()))
            .ToList();

    /// <summary>
    /// The extension's client assembly, fetched by the browser before the
    /// router is built.
    /// </summary>
    /// <remarks>
    /// Under <c>/api/</c> deliberately: the Electron shell proxies that prefix
    /// to the configured instance, so the desktop app picks up extensions from
    /// the server it is pointed at with no shell change. Same-origin either way,
    /// so <c>connect-src 'self'</c> already covers the fetch.
    /// </remarks>
    [HttpGet("{extensionId}/client/{fileName}")]
    [AllowAnonymous]
    public IActionResult GetClientAssembly(string extensionId, string fileName)
    {
        var package = state.Enabled.FirstOrDefault(p =>
            string.Equals(p.Id, extensionId, StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            return NotFound();
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var main = Path.GetFileNameWithoutExtension(package.Manifest.Client);

        byte[]? bytes = string.Equals(name, main, StringComparison.OrdinalIgnoreCase)
            ? package.ClientAssembly
            : package.ClientDependencies.GetValueOrDefault(name);

        return bytes is null ? NotFound() : File(bytes, "application/octet-stream");
    }

    /// <summary>Static assets from the package's <c>content/</c> folder.</summary>
    [HttpGet("{extensionId}/content/{**path}")]
    [AllowAnonymous]
    public IActionResult GetContent(string extensionId, string path)
    {
        var package = state.Enabled.FirstOrDefault(p =>
            string.Equals(p.Id, extensionId, StringComparison.OrdinalIgnoreCase));
        if (package is null || !package.Content.TryGetValue(path, out var content))
        {
            return NotFound();
        }

        return File(content.Bytes, content.ContentType);
    }

    /// <summary>
    /// Everything the instance knows about extensions, including ones whose
    /// package is gone but whose data survives.
    /// </summary>
    [HttpGet("admin")]
    [Authorize(Policy = AuthConstants.SuperAdminPolicy)]
    public async Task<ActionResult<ExtensionAdminDto>> Admin(CancellationToken ct)
    {
        var rows = await registry.ListAsync(ct);
        var extensions = rows.Select(row =>
        {
            var package = state.Find(row.ExtensionId);
            var loadedThisBoot = package is not null;
            return new ExtensionRegistrationDto(
                row.ExtensionId,
                row.DisplayName,
                row.LastSeenVersion,
                row.SchemaName,
                row.State.ToString(),
                row.FailureReason,
                row.InstalledAt,
                row.LastLoadedAt,
                package?.Manifest.Hooks ?? [],
                // Purge only for something not loaded this boot: nothing in the
                // process is holding a DbContext against the schema, so the drop
                // needs no locking and no coordination.
                CanPurge: !loadedThisBoot);
        }).ToList();

        return new ExtensionAdminDto(
            extensions,
            state.Failures.Select(f => new ExtensionFailureDto(f.Source, f.Reason)).ToList());
    }

    /// <summary>
    /// Drops an extension's schema and strips its keys from every core field
    /// bag. Irreversible.
    /// </summary>
    /// <remarks>
    /// <para>Guarded three ways: super admin only, the caller retypes the
    /// extension id, and the extension must not be loaded this boot. The last is
    /// what keeps the drop safe without locking — remove the package (or let a
    /// failed load disable it), restart, then purge.</para>
    ///
    /// <para>Audited explicitly. A schema drop is raw DDL that
    /// <c>AuditSaveChangesInterceptor</c> never observes, so the most
    /// destructive action in the admin UI would otherwise leave no trace.</para>
    /// </remarks>
    [HttpDelete("{extensionId}/data")]
    [Authorize(Policy = AuthConstants.SuperAdminPolicy)]
    public async Task<IActionResult> Purge(
        string extensionId, PurgeExtensionRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.ConfirmExtensionId, extensionId, StringComparison.Ordinal))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Confirmation does not match",
                detail: "Type the extension's id exactly to confirm the purge.");
        }

        var rows = await registry.ListAsync(ct);
        var row = rows.FirstOrDefault(r =>
            string.Equals(r.ExtensionId, extensionId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return NotFound();
        }

        if (state.Find(extensionId) is not null)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Extension is still installed",
                detail: "Remove the package from the extensions directory and restart the instance, " +
                        "then purge its data. Dropping a schema the running app still holds a " +
                        "connection to is not something this will do.");
        }

        logger.LogWarning(
            "Purging extension {Id} (schema {Schema}) at the request of {User}.",
            row.ExtensionId, row.SchemaName, User.Identity?.Name);

        await registry.PurgeAsync(row, ct);
        return NoContent();
    }
}
