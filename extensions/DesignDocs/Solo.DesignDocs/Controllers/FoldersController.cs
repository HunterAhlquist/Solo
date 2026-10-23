using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.DesignDocs.Live;
using Solo.Extensibility;

namespace Solo.DesignDocs.Controllers;

/// <summary>Folders in an open vault, for the project's writers.</summary>
[Route("projects/{projectId:guid}/vaults/{vaultId:guid}/folders")]
public sealed class FoldersController(
    DesignDocsDbContext db, ISoloLiveUpdates live, DocumentLog log, DocumentDirectory directory)
    : DocsControllerBase(db, live)
{
    [HttpPost]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<FolderDto>> Create(
        Guid projectId, Guid vaultId, CreateFolderRequest request, CancellationToken ct)
    {
        var (vault, problem) = await OpenVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return problem!;
        }

        if (DocNames.Clean(request.Name, out var nameProblem) is not { } name)
        {
            return BadName(nameProblem);
        }

        if (request.ParentId is { } parentId && !await Db.Folders.AnyAsync(f => f.Id == parentId && f.VaultId == vaultId, ct))
        {
            return NotFound();
        }

        var folder = new Folder { VaultId = vaultId, ParentId = request.ParentId, Name = name };
        Db.Folders.Add(folder);
        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            return NameTaken("a folder");
        }

        await TreeChangedAsync(projectId, vaultId, ct);
        return StatusCode(StatusCodes.Status201Created, folder.ToDto());
    }

    /// <summary>Rename and move in one. A folder can't move into itself or anything inside it.</summary>
    [HttpPut("{folderId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<FolderDto>> Update(
        Guid projectId, Guid vaultId, Guid folderId, UpdateNodeRequest request, CancellationToken ct)
    {
        var (vault, problem) = await OpenVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return problem!;
        }

        if (DocNames.Clean(request.Name, out var nameProblem) is not { } name)
        {
            return BadName(nameProblem);
        }

        var folder = await Db.Folders.FirstOrDefaultAsync(f => f.Id == folderId && f.VaultId == vaultId, ct);
        if (folder is null)
        {
            return NotFound();
        }

        if (request.ParentId is { } parentId)
        {
            var parents = await Db.Folders.AsNoTracking().Where(f => f.VaultId == vaultId)
                .ToDictionaryAsync(f => f.Id, f => f.ParentId, ct);
            if (!parents.ContainsKey(parentId))
            {
                return NotFound();
            }

            for (Guid? at = parentId; at is { } current; at = parents.GetValueOrDefault(current))
            {
                if (current == folderId)
                {
                    return Invalid("Can't move there", "A folder can't go inside itself.");
                }
            }
        }

        folder.Name = name;
        folder.ParentId = request.ParentId;
        Db.Entry(folder).Property(f => f.Version).OriginalValue = request.Version;
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
            return NameTaken("a folder");
        }

        await TreeChangedAsync(projectId, vaultId, ct);
        return folder.ToDto();
    }

    /// <summary>The folder and everything in it: subfolders, notes, their sketches and their logs.</summary>
    [HttpDelete("{folderId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> Delete(Guid projectId, Guid vaultId, Guid folderId, CancellationToken ct)
    {
        var (vault, problem) = await OpenVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return problem!;
        }

        var parents = await Db.Folders.AsNoTracking().Where(f => f.VaultId == vaultId)
            .Select(f => new { f.Id, f.ParentId }).ToListAsync(ct);
        if (parents.All(f => f.Id != folderId))
        {
            return NotFound();
        }

        var inside = new HashSet<Guid> { folderId };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var folder in parents)
            {
                if (folder.ParentId is { } parent && inside.Contains(parent) && inside.Add(folder.Id))
                {
                    grew = true;
                }
            }
        }

        var notes = await Db.Notes.Where(n => n.VaultId == vaultId && n.FolderId != null && inside.Contains(n.FolderId.Value))
            .Select(n => n.Id).ToListAsync(ct);
        var documents = notes
            .Concat(await Db.Sketches.Where(s => notes.Contains(s.NoteId)).Select(s => s.Id).ToListAsync(ct))
            .ToList();

        await using (var transaction = await Db.Database.BeginTransactionAsync(ct))
        {
            await log.DeleteAsync(documents, ct);
            // Subfolders, notes and sketches go with it by cascade.
            await Db.Folders.Where(f => f.Id == folderId).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        }

        foreach (var document in documents)
        {
            directory.Forget(document);
            await PublishAsync(projectId, DocsTopics.Document(document), DocsTopics.Deleted, ct);
        }

        await TreeChangedAsync(projectId, vaultId, ct);
        return NoContent();
    }
}
