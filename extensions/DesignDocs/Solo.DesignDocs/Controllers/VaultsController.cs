using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.DesignDocs.Live;
using Solo.Extensibility;

namespace Solo.DesignDocs.Controllers;

/// <summary>
/// A project's design docs (vaults), at <c>/api/ext/design-docs/projects/{projectId}/vaults</c>,
/// and each one's tree.
/// </summary>
/// <remarks>
/// <para>Creating, renaming, closing, reopening and deleting a vault is the
/// super admin's, as a board's and a roadmap's are (PR #29). The owner policy
/// stacked on top is there so the project resolves from the route: a super
/// admin passes it whether or not they're a member, so the effective rule is
/// "super admins only".</para>
///
/// <para>What's inside a vault belongs to the project's writers; see
/// <see cref="FoldersController"/> and <see cref="NotesController"/>.</para>
/// </remarks>
[Route("projects/{projectId:guid}/vaults")]
public sealed class VaultsController(
    DesignDocsDbContext db, ISoloQuery query, ISoloLiveUpdates live, DocumentDirectory directory)
    : DocsControllerBase(db, live)
{
    [HttpGet]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<VaultListDto> List(Guid projectId, CancellationToken ct)
    {
        var vaults = await Db.Vaults.AsNoTracking()
            .Where(v => v.ProjectId == projectId)
            .OrderBy(v => v.Name)
            .ToListAsync(ct);
        return new VaultListDto((await query.CallerAsync(ct)).IsSuperAdmin, vaults.Select(v => v.ToDto()).ToList());
    }

    [HttpPost]
    [Authorize(Policy = SoloPolicies.SuperAdmin)]
    [Authorize(Policy = SoloPolicies.ProjectOwner)]
    public async Task<ActionResult<VaultDto>> Create(Guid projectId, CreateVaultRequest request, CancellationToken ct)
    {
        if (DocNames.Clean(request.Name, out var problem) is not { } name)
        {
            return BadName(problem);
        }

        var vault = new Vault { ProjectId = projectId, Name = name, CreatedBy = (await query.CallerAsync(ct)).UserId };
        Db.Vaults.Add(vault);
        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            return NameTaken("a design doc");
        }

        await PublishAsync(projectId, DocsTopics.Vaults, DocsTopics.Changed, ct);
        return CreatedAtAction(nameof(Tree), new { projectId, vaultId = vault.Id }, vault.ToDto());
    }

    [HttpPut("{vaultId:guid}")]
    [Authorize(Policy = SoloPolicies.SuperAdmin)]
    [Authorize(Policy = SoloPolicies.ProjectOwner)]
    public async Task<ActionResult<VaultDto>> Rename(
        Guid projectId, Guid vaultId, RenameRequest request, CancellationToken ct)
    {
        if (DocNames.Clean(request.Name, out var problem) is not { } name)
        {
            return BadName(problem);
        }

        var vault = await FindVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return NotFound();
        }

        vault.Name = name;
        Db.Entry(vault).Property(v => v.Version).OriginalValue = request.Version;
        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChangedElsewhere();
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            return NameTaken("a design doc");
        }

        await PublishAsync(projectId, DocsTopics.Vaults, DocsTopics.Changed, ct);
        await PublishAsync(projectId, DocsTopics.Vault(vaultId), DocsTopics.Renamed, ct);
        return vault.ToDto();
    }

    [HttpPost("{vaultId:guid}/close")]
    [Authorize(Policy = SoloPolicies.SuperAdmin)]
    [Authorize(Policy = SoloPolicies.ProjectOwner)]
    public Task<ActionResult<VaultDto>> Close(Guid projectId, Guid vaultId, CancellationToken ct) =>
        SetClosedAsync(projectId, vaultId, closed: true, ct);

    [HttpPost("{vaultId:guid}/reopen")]
    [Authorize(Policy = SoloPolicies.SuperAdmin)]
    [Authorize(Policy = SoloPolicies.ProjectOwner)]
    public Task<ActionResult<VaultDto>> Reopen(Guid projectId, Guid vaultId, CancellationToken ct) =>
        SetClosedAsync(projectId, vaultId, closed: false, ct);

    /// <summary>Only a closed vault can be deleted, so a delete is always two deliberate steps.</summary>
    [HttpDelete("{vaultId:guid}")]
    [Authorize(Policy = SoloPolicies.SuperAdmin)]
    [Authorize(Policy = SoloPolicies.ProjectOwner)]
    public async Task<IActionResult> Delete(Guid projectId, Guid vaultId, CancellationToken ct)
    {
        var vault = await FindVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return NotFound();
        }

        if (!vault.IsClosed)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Design doc still open",
                detail: "Close the design doc before deleting it.");
        }

        // Everything in the schema hangs off the vault and cascades with it.
        await Db.Vaults.Where(v => v.Id == vaultId).ExecuteDeleteAsync(ct);
        directory.ForgetVault(vaultId);
        await PublishAsync(projectId, DocsTopics.Vaults, DocsTopics.Changed, ct);
        await PublishAsync(projectId, DocsTopics.Vault(vaultId), DocsTopics.Deleted, ct);
        return NoContent();
    }

    /// <summary>Everything the vault page draws its tree from and resolves links and embeds against.</summary>
    [HttpGet("{vaultId:guid}/tree")]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<ActionResult<VaultTreeDto>> Tree(Guid projectId, Guid vaultId, CancellationToken ct)
    {
        var vault = await Db.Vaults.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vaultId && v.ProjectId == projectId, ct);
        if (vault is null)
        {
            return NotFound();
        }

        var folders = await Db.Folders.AsNoTracking().Where(f => f.VaultId == vaultId)
            .OrderBy(f => f.Name).Select(f => f.ToDto()).ToListAsync(ct);
        var notes = await Db.Notes.AsNoTracking().Where(n => n.VaultId == vaultId)
            .OrderBy(n => n.Name)
            .Select(n => new NoteSummaryDto(n.Id, n.FolderId, n.Name, n.LastEditedBy, n.LastEditedAt, n.Version))
            .ToListAsync(ct);
        var sketches = await Db.Sketches.AsNoTracking().Where(s => s.VaultId == vaultId)
            .OrderBy(s => s.Name).Select(s => s.ToSummary()).ToListAsync(ct);
        var images = await Db.Images.AsNoTracking().Where(i => i.VaultId == vaultId)
            .OrderBy(i => i.Path)
            .Select(i => new ImageSummaryDto(i.Id, i.Path, i.ContentType, i.Width, i.Height, i.Bytes, i.Version))
            .ToListAsync(ct);

        return new VaultTreeDto(vault.ToDto(), (await query.CallerAsync(ct)).IsSuperAdmin, folders, notes, sketches, images);
    }

    private async Task<ActionResult<VaultDto>> SetClosedAsync(Guid projectId, Guid vaultId, bool closed, CancellationToken ct)
    {
        var vault = await FindVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return NotFound();
        }

        if (vault.IsClosed != closed)
        {
            vault.IsClosed = closed;
            vault.ClosedAt = closed ? DateTimeOffset.UtcNow : null;
            await Db.SaveChangesAsync(ct);
        }

        // Before the nudge, so the live handler refuses the next write rather
        // than the one after its cache expires.
        directory.ForgetVault(vaultId);
        await PublishAsync(projectId, DocsTopics.Vaults, DocsTopics.Changed, ct);
        await PublishAsync(projectId, DocsTopics.Vault(vaultId), closed ? DocsTopics.Closed : DocsTopics.Reopened, ct);
        return vault.ToDto();
    }
}
