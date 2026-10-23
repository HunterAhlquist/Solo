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
/// Sketches' rows: name, owning note and page. The strokes themselves are a
/// live document like a note's text, on the sketch's own topic.
/// </summary>
[Route("projects/{projectId:guid}/vaults/{vaultId:guid}/sketches")]
public sealed class SketchesController(
    DesignDocsDbContext db, ISoloLiveUpdates live, DocumentLog log, DocumentDirectory directory)
    : DocsControllerBase(db, live)
{
    [HttpPost]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<SketchSummaryDto>> Create(
        Guid projectId, Guid vaultId, CreateSketchRequest request, CancellationToken ct)
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

        if (PageProblem(request.PageSize, request.Orientation, request.Pattern) is { } pageProblem)
        {
            return pageProblem;
        }

        if (!await Db.Notes.AnyAsync(n => n.Id == request.NoteId && n.VaultId == vaultId, ct))
        {
            return NotFound();
        }

        var sketch = new Sketch
        {
            VaultId = vaultId,
            NoteId = request.NoteId,
            Name = name,
            PageSize = request.PageSize,
            Orientation = request.Orientation,
            Pattern = request.Pattern,
        };
        Db.Sketches.Add(sketch);
        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            return NameTaken("a sketch");
        }

        await TreeChangedAsync(projectId, vaultId, ct);
        return StatusCode(StatusCodes.Status201Created, sketch.ToSummary());
    }

    /// <summary>Rename, or change the page. Shrinking a page clips nothing stored; the strokes are only hidden.</summary>
    [HttpPut("{sketchId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<SketchSummaryDto>> Update(
        Guid projectId, Guid vaultId, Guid sketchId, UpdateSketchRequest request, CancellationToken ct)
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

        if (PageProblem(request.PageSize, request.Orientation, request.Pattern) is { } pageProblem)
        {
            return pageProblem;
        }

        var sketch = await Db.Sketches.FirstOrDefaultAsync(s => s.Id == sketchId && s.VaultId == vaultId, ct);
        if (sketch is null)
        {
            return NotFound();
        }

        sketch.Name = name;
        sketch.PageSize = request.PageSize;
        sketch.Orientation = request.Orientation;
        sketch.Pattern = request.Pattern;
        Db.Entry(sketch).Property(s => s.Version).OriginalValue = request.Version;
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
            return NameTaken("a sketch");
        }

        await TreeChangedAsync(projectId, vaultId, ct);
        await PublishAsync(projectId, DocsTopics.Document(sketchId), DocsTopics.Renamed, ct);
        return sketch.ToSummary();
    }

    [HttpDelete("{sketchId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> Delete(Guid projectId, Guid vaultId, Guid sketchId, CancellationToken ct)
    {
        var (vault, problem) = await OpenVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return problem!;
        }

        if (!await Db.Sketches.AnyAsync(s => s.Id == sketchId && s.VaultId == vaultId, ct))
        {
            return NotFound();
        }

        await using (var transaction = await Db.Database.BeginTransactionAsync(ct))
        {
            await log.DeleteAsync([sketchId], ct);
            await Db.Sketches.Where(s => s.Id == sketchId).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        }

        directory.Forget(sketchId);
        await PublishAsync(projectId, DocsTopics.Document(sketchId), DocsTopics.Deleted, ct);
        await TreeChangedAsync(projectId, vaultId, ct);
        return NoContent();
    }

    private ObjectResult? PageProblem(string size, string orientation, string pattern) =>
        !SketchPage.Sizes.Contains(size) ? Invalid("Unknown page size", $"Choose one of {string.Join(", ", SketchPage.Sizes)}.")
        : !SketchPage.Orientations.Contains(orientation) ? Invalid("Unknown orientation", "Choose portrait or landscape.")
        : !SketchPage.Patterns.Contains(pattern) ? Invalid("Unknown pattern", $"Choose one of {string.Join(", ", SketchPage.Patterns)}.")
        : null;
}
