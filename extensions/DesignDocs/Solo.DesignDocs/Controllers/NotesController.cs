using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Notes: their place in the tree, the markdown copy writers send, and the
/// stored log for an exact export.
/// </summary>
/// <remarks>
/// The note's text itself travels over the live topic (see
/// <see cref="DocumentSyncHandler"/>). What's stored here is what the server
/// can know without decoding it: the copy and headings a writer's client
/// worked out, and who sent it last.
/// </remarks>
[Route("projects/{projectId:guid}/vaults/{vaultId:guid}")]
public sealed class NotesController(
    DesignDocsDbContext db, ISoloQuery query, ISoloLiveUpdates live, DocumentLog log, DocumentDirectory directory)
    : DocsControllerBase(db, live)
{
    [HttpPost("notes")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<NoteSummaryDto>> Create(
        Guid projectId, Guid vaultId, CreateNoteRequest request, CancellationToken ct)
    {
        var (vault, problem) = await OpenVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return problem!;
        }

        if (DocNames.CleanNote(request.Name, out var nameProblem) is not { } name)
        {
            return BadName(nameProblem);
        }

        if (request.FolderId is { } folderId && !await Db.Folders.AnyAsync(f => f.Id == folderId && f.VaultId == vaultId, ct))
        {
            return NotFound();
        }

        var note = new Note { VaultId = vaultId, FolderId = request.FolderId, Name = name };
        Db.Notes.Add(note);
        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            return NameTaken("a note");
        }

        await TreeChangedAsync(projectId, vaultId, ct);
        return CreatedAtAction(nameof(Get), new { projectId, vaultId, noteId = note.Id }, note.ToSummary());
    }

    [HttpGet("notes/{noteId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<ActionResult<NoteDto>> Get(Guid projectId, Guid vaultId, Guid noteId, CancellationToken ct)
    {
        if (await FindVaultAsync(projectId, vaultId, ct) is null)
        {
            return NotFound();
        }

        var note = await Db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId && n.VaultId == vaultId, ct);
        if (note is null)
        {
            return NotFound();
        }

        string? editorName = null;
        if (note.LastEditedBy is { } editor)
        {
            editorName = (await query.MembersAsync(projectId, ct)).FirstOrDefault(m => m.UserId == editor)?.DisplayName;
        }

        return new NoteDto(
            note.Id, note.VaultId, note.FolderId, note.Name,
            note.SeedMarkdown ?? note.Markdown,
            ReadHeadings(note.Headings),
            note.SeedMarkdown is null,
            note.LastEditedBy, editorName, note.LastEditedAt, note.Version);
    }

    /// <summary>Rename and move in one. The id, and so every link to the note, stays the same.</summary>
    [HttpPut("notes/{noteId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<NoteSummaryDto>> Update(
        Guid projectId, Guid vaultId, Guid noteId, UpdateNodeRequest request, CancellationToken ct)
    {
        var (vault, problem) = await OpenVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return problem!;
        }

        if (DocNames.CleanNote(request.Name, out var nameProblem) is not { } name)
        {
            return BadName(nameProblem);
        }

        var note = await Db.Notes.FirstOrDefaultAsync(n => n.Id == noteId && n.VaultId == vaultId, ct);
        if (note is null ||
            (request.ParentId is { } folderId && !await Db.Folders.AnyAsync(f => f.Id == folderId && f.VaultId == vaultId, ct)))
        {
            return NotFound();
        }

        var renamed = note.Name != name;
        note.Name = name;
        note.FolderId = request.ParentId;
        Db.Entry(note).Property(n => n.Version).OriginalValue = request.Version;
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
            return NameTaken("a note");
        }

        await TreeChangedAsync(projectId, vaultId, ct);
        if (renamed)
        {
            await PublishAsync(projectId, DocsTopics.Document(noteId), DocsTopics.Renamed, ct);
        }

        return note.ToSummary();
    }

    /// <summary>The note, its sketches, and their logs.</summary>
    [HttpDelete("notes/{noteId:guid}")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> Delete(Guid projectId, Guid vaultId, Guid noteId, CancellationToken ct)
    {
        var (vault, problem) = await OpenVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return problem!;
        }

        if (!await Db.Notes.AnyAsync(n => n.Id == noteId && n.VaultId == vaultId, ct))
        {
            return NotFound();
        }

        var documents = await Db.Sketches.Where(s => s.NoteId == noteId).Select(s => s.Id).ToListAsync(ct);
        documents.Add(noteId);
        await using (var transaction = await Db.Database.BeginTransactionAsync(ct))
        {
            await log.DeleteAsync(documents, ct);
            await Db.Notes.Where(n => n.Id == noteId).ExecuteDeleteAsync(ct);
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

    /// <summary>
    /// The markdown copy and headings a writer's client sends after its own
    /// edits. An older copy than the stored one is ignored, so copies arriving
    /// out of order can't move the text backwards.
    /// </summary>
    [HttpPut("notes/{noteId:guid}/copy")]
    [Authorize(Policy = SoloPolicies.ProjectWrite)]
    [RequestSizeLimit(4 * DocsLimits.MarkdownCopy)]
    public async Task<IActionResult> Copy(
        Guid projectId, Guid vaultId, Guid noteId, NoteCopyRequest request, CancellationToken ct)
    {
        var (vault, problem) = await OpenVaultAsync(projectId, vaultId, ct);
        if (vault is null)
        {
            return problem!;
        }

        var caller = await query.CallerAsync(ct);
        var headings = JsonSerializer.Serialize(request.Headings
            .Select(h => new NoteHeadingDto(Math.Clamp(h.Level, 1, 6), Truncate(h.Text, 500), Truncate(h.Slug, 500))),
            JsonSerializerOptions.Web);
        var now = DateTimeOffset.UtcNow;
        var updated = await Db.Notes
            .Where(n => n.Id == noteId && n.VaultId == vaultId && n.MarkdownSeq <= request.Seq)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Markdown, request.Markdown)
                .SetProperty(n => n.MarkdownSeq, request.Seq)
                .SetProperty(n => n.Headings, headings)
                .SetProperty(n => n.LastEditedBy, caller.UserId)
                .SetProperty(n => n.LastEditedAt, now), ct);
        if (updated == 0 && !await Db.Notes.AnyAsync(n => n.Id == noteId && n.VaultId == vaultId, ct))
        {
            return NotFound();
        }

        if (updated > 0)
        {
            await PublishAsync(projectId, DocsTopics.Document(noteId), DocsTopics.Edited, ct);
        }

        return NoContent();
    }

    /// <summary>
    /// A note's or sketch's stored log, for an export that is exact rather than
    /// the copy, which can lag. Includes the seed of a note nobody has opened
    /// since it was imported, so it exports byte for byte as it came in.
    /// </summary>
    [HttpGet("documents/{documentId:guid}/log")]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<ActionResult<DocumentLogDto>> Log(Guid projectId, Guid vaultId, Guid documentId, CancellationToken ct)
    {
        if (await FindVaultAsync(projectId, vaultId, ct) is null)
        {
            return NotFound();
        }

        var seed = await Db.Notes.AsNoTracking().Where(n => n.Id == documentId && n.VaultId == vaultId)
            .Select(n => new { n.SeedMarkdown }).FirstOrDefaultAsync(ct);
        if (seed is null && !await Db.Sketches.AnyAsync(s => s.Id == documentId && s.VaultId == vaultId, ct))
        {
            return NotFound();
        }

        var entries = await log.ReadAsync(documentId, 0, ct);
        return new DocumentLogDto(
            entries.Select(e => new LogEntryDto(e.Seq, e.IsSnapshot, Convert.ToBase64String(e.Data))).ToList(),
            entries.Count == 0 ? seed?.SeedMarkdown : null);
    }

    /// <summary>
    /// How many other notes link to this one by name with <c>[[...]]</c>, so a
    /// rename can warn that those links will stop resolving. Counted from the
    /// stored copies, which is close enough for a warning.
    /// </summary>
    [HttpGet("notes/{noteId:guid}/references")]
    [Authorize(Policy = SoloPolicies.ProjectRead)]
    public async Task<ActionResult<int>> References(Guid projectId, Guid vaultId, Guid noteId, CancellationToken ct)
    {
        if (await FindVaultAsync(projectId, vaultId, ct) is null)
        {
            return NotFound();
        }

        var note = await Db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId && n.VaultId == vaultId, ct);
        if (note is null)
        {
            return NotFound();
        }

        var needle = "[[" + note.Name;
        var candidates = await Db.Notes.AsNoTracking()
            .Where(n => n.VaultId == vaultId && n.Id != noteId &&
                        ((n.SeedMarkdown ?? n.Markdown).Contains(needle) || (n.SeedMarkdown ?? n.Markdown).Contains("/" + note.Name)))
            .Select(n => n.SeedMarkdown ?? n.Markdown)
            .ToListAsync(ct);

        // [[Name]], [[Name|text]], [[Name#Heading]], [[Folder/Name]], each optionally embedded.
        var pattern = new Regex(@"\[\[(?:[^\]|#]*/)?" + Regex.Escape(note.Name) + @"(?:\.md)?(?:[#|][^\]]*)?\]\]",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return candidates.Count(pattern.IsMatch);
    }

    internal static IReadOnlyList<NoteHeadingDto> ReadHeadings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<NoteHeadingDto>>(json, JsonSerializerOptions.Web) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Truncate(string? value, int length) =>
        value is null ? "" : value.Length <= length ? value : value[..length];
}
