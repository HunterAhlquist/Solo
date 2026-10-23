using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.Extensibility;

namespace Solo.DesignDocs.Controllers;

/// <summary>
/// Notes from an Obsidian vault, a batch at a time. The browser reads the zip;
/// this only creates the folders and notes it names.
/// </summary>
/// <remarks>
/// Each note's text is kept as its seed, unchanged, front matter and all. The
/// server can't build a live document from markdown, so the first writer to
/// open the note does (see <c>DocumentSyncHandler</c>). Until then the note
/// exports exactly as it was imported.
/// </remarks>
[Route("projects/{projectId:guid}/vaults/{vaultId:guid}/import")]
public sealed class ImportController(DesignDocsDbContext db, ISoloLiveUpdates live) : DocsControllerBase(db, live)
{
    [HttpPost("notes")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    [RequestSizeLimit(3 * DocsLimits.ImportBytes)]
    public async Task<ActionResult<ImportResultDto>> Notes(
        Guid projectId, Guid vaultId, ImportNotesRequest request, CancellationToken ct)
    {
        var (vault, problem) = await OpenVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return problem!;
        }

        if (request.Notes.Sum(n => (long)n.Markdown.Length) > DocsLimits.ImportBytes)
        {
            return Invalid("Batch too large", $"Send at most {DocsLimits.ImportBytes / (1024 * 1024)} MB of notes per call.");
        }

        var folders = await Db.Folders.Where(f => f.VaultId == vaultId).ToListAsync(ct);
        var created = 0;
        var skipped = new List<string>();

        // Folders the batch names on their own, plus each note's own folder.
        Guid? FolderFor(string[] segments)
        {
            Guid? parent = null;
            foreach (var segment in segments)
            {
                var existing = folders.FirstOrDefault(f => f.ParentId == parent && f.Name == segment);
                if (existing is null)
                {
                    existing = new Folder { VaultId = vaultId, ParentId = parent, Name = segment };
                    folders.Add(existing);
                    Db.Folders.Add(existing);
                    created++;
                }

                parent = existing.Id;
            }

            return parent;
        }

        foreach (var path in request.Folders)
        {
            if (DocNames.CleanPath(path, out _) is { } clean)
            {
                FolderFor(clean.Split('/'));
            }
            else
            {
                skipped.Add($"{path}: not a valid folder name");
            }
        }

        var notes = await Db.Notes.Where(n => n.VaultId == vaultId)
            .Select(n => new { n.FolderId, n.Name }).ToListAsync(ct);
        var taken = notes.Select(n => (n.FolderId, n.Name)).ToHashSet();
        var notesCreated = 0;
        foreach (var import in request.Notes)
        {
            var path = import.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? import.Path[..^3] : import.Path;
            if (DocNames.CleanPath(path, out var pathProblem) is not { } clean)
            {
                skipped.Add($"{import.Path}: {pathProblem}");
                continue;
            }

            var segments = clean.Split('/');
            var folderId = FolderFor(segments[..^1]);
            var name = segments[^1];
            if (!taken.Add((folderId, name)))
            {
                skipped.Add($"{import.Path}: a note with that name is already there");
                continue;
            }

            // The copy starts as the seed, so link labels and the rename warning
            // work before anyone opens the note.
            Db.Notes.Add(new Note
            {
                VaultId = vaultId,
                FolderId = folderId,
                Name = name,
                SeedMarkdown = import.Markdown,
                Markdown = import.Markdown,
            });
            notesCreated++;
        }

        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            // Someone else created one of these names meanwhile. Nothing in the
            // batch was saved, so the client can send it again.
            return ChangedElsewhere();
        }

        await TreeChangedAsync(projectId, vaultId, ct);
        return new ImportResultDto(created, notesCreated, skipped);
    }
}
