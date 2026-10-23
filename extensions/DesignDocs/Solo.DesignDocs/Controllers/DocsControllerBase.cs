using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.Extensibility;

namespace Solo.DesignDocs.Controllers;

/// <summary>
/// What every Design Docs controller shares: finding a vault only under its
/// own project, the problem answers, and the live nudges after a save.
/// </summary>
/// <remarks>
/// A vault, folder, note or image id is honored only under the route of the
/// project it belongs to. Every lookup filters on the route's project id and
/// answers 404 otherwise, so a member of one project can't pass another
/// project's ids under a route they're allowed on.
/// </remarks>
[ApiController]
public abstract class DocsControllerBase(DesignDocsDbContext db, ISoloLiveUpdates live) : ControllerBase
{
    protected DesignDocsDbContext Db => db;

    protected Task<Vault?> FindVaultAsync(Guid projectId, Guid vaultId, CancellationToken ct) =>
        db.Vaults.FirstOrDefaultAsync(v => v.Id == vaultId && v.ProjectId == projectId, ct);

    /// <summary>The vault under its project, or why not: missing (404) or closed (409).</summary>
    protected async Task<(Vault? Vault, ActionResult? Problem)> OpenVaultAsync(
        Guid projectId, Guid vaultId, CancellationToken ct)
    {
        var vault = await FindVaultAsync(projectId, vaultId, ct);
        return vault is null ? (null, NotFound())
            : vault.IsClosed ? (null, VaultClosed())
            : (vault, null);
    }

    protected Task TreeChangedAsync(Guid projectId, Guid vaultId, CancellationToken ct) =>
        live.PublishAsync(projectId, DocsTopics.Vault(vaultId), DocsTopics.Tree, ct: ct);

    protected Task PublishAsync(Guid projectId, string topic, string kind, CancellationToken ct, string? data = null) =>
        live.PublishAsync(projectId, topic, kind, data, ct);

    protected ObjectResult ChangedElsewhere() => Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Concurrency conflict",
        detail: "Someone else changed this first. Re-fetch and try again.");

    protected ObjectResult VaultClosed() => Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Design doc closed",
        detail: "This design doc is closed, so it's read-only. A super admin can reopen it.");

    protected ObjectResult NameTaken(string what) => Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Name taken",
        detail: $"There's already {what} with that name here.");

    protected ObjectResult BadName(string? problem) => Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid name",
        detail: problem);

    protected ObjectResult Invalid(string title, string detail) => Problem(
        statusCode: StatusCodes.Status400BadRequest, title: title, detail: detail);

    protected static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
