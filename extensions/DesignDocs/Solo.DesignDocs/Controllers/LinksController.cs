using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.Extensibility;
using Solo.Shared;

namespace Solo.DesignDocs.Controllers;

/// <summary>
/// What links need: where a note is, given only its id, the labels for doc
/// links in core's markdown, and the tasks that doc chips show.
/// </summary>
/// <remarks>
/// <para>These routes carry no project id, because a link carries only the
/// note's or task's id. So they can't use the project policies: a plain
/// <c>[Authorize]</c> admits any provisioned user, and every answer is
/// filtered to the projects <see cref="ISoloQuery"/> says the caller can
/// read.</para>
///
/// <para>A note the caller can't read answers exactly as a missing one does,
/// so these routes never reveal that a note exists.</para>
/// </remarks>
public sealed partial class LinksController(DesignDocsDbContext db, ISoloQuery query, ISoloLiveUpdates live)
    : DocsControllerBase(db, live)
{
    [HttpGet("notes/{noteId:guid}/locate")]
    [Authorize]
    public async Task<ActionResult<NoteLocationDto>> Locate(Guid noteId, CancellationToken ct)
    {
        var found = await Db.Notes.AsNoTracking()
            .Where(n => n.Id == noteId)
            .Join(Db.Vaults, n => n.VaultId, v => v.Id, (n, v) => new { n.Id, n.Name, v.ProjectId, VaultId = v.Id })
            .FirstOrDefaultAsync(ct);
        if (found is null)
        {
            return NotFound();
        }

        var project = (await query.ProjectsAsync(ct)).FirstOrDefault(p => p.Id == found.ProjectId);
        return project is null
            ? NotFound()
            : new NoteLocationDto(found.Id, project.Id, project.Alias, found.VaultId, found.Name);
    }

    /// <summary>
    /// Current labels for <c>/docs/note/{id}</c> links: the note's name, and the
    /// heading's text after a <c>›</c> when the link names one that still exists.
    /// Links to notes the caller can't read are left out.
    /// </summary>
    [HttpPost("links/labels")]
    [Authorize]
    public async Task<IReadOnlyDictionary<string, string>> Labels(LinkLabelsRequest request, CancellationToken ct)
    {
        var links = request.Urls
            .Select(url => (Url: url, Match: NoteLink().Match(url)))
            .Where(l => l.Match.Success && Guid.TryParse(l.Match.Groups["id"].Value, out _))
            .Select(l => (l.Url, Id: Guid.Parse(l.Match.Groups["id"].Value),
                Slug: l.Match.Groups["slug"].Success ? Uri.UnescapeDataString(l.Match.Groups["slug"].Value) : null))
            .ToList();
        if (links.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var ids = links.Select(l => l.Id).Distinct().ToList();
        var readable = (await query.ProjectsAsync(ct)).Select(p => p.Id).ToHashSet();
        var notes = await Db.Notes.AsNoTracking()
            .Where(n => ids.Contains(n.Id))
            .Join(Db.Vaults, n => n.VaultId, v => v.Id, (n, v) => new { n.Id, n.Name, n.Headings, v.ProjectId })
            .ToListAsync(ct);
        var byId = notes.Where(n => readable.Contains(n.ProjectId)).ToDictionary(n => n.Id);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (url, id, slug) in links)
        {
            if (!byId.TryGetValue(id, out var note))
            {
                continue;
            }

            var heading = slug is null
                ? null
                : NotesController.ReadHeadings(note.Headings).FirstOrDefault(h => h.Slug == slug);
            labels[url] = heading is null ? note.Name : $"{note.Name} › {heading.Text}";
        }

        return labels;
    }

    /// <summary>The tasks doc chips show, with their serials as core writes them. Unreadable tasks are left out.</summary>
    [HttpPost("tasks/lookup")]
    [Authorize]
    public async Task<IReadOnlyList<TaskLookupDto>> Tasks(TaskLookupRequest request, CancellationToken ct)
    {
        var found = new List<TaskLookupDto>();
        Dictionary<Guid, (string Project, string Board)>? aliases = null;
        foreach (var id in request.Ids.Distinct())
        {
            if (await query.TaskAsync(id, ct) is not { } task)
            {
                continue;
            }

            aliases ??= await BoardAliasesAsync(ct);
            if (aliases.TryGetValue(task.BoardId, out var alias))
            {
                found.Add(new TaskLookupDto(task.Id, Aliases.Serial(alias.Project, alias.Board, task.Number), task.Name,
                    task.Status == TaskState.Finished));
            }
        }

        return found;
    }

    /// <summary>Each readable board's project and board alias, which make up a task's serial.</summary>
    private async Task<Dictionary<Guid, (string Project, string Board)>> BoardAliasesAsync(CancellationToken ct)
    {
        var aliases = new Dictionary<Guid, (string, string)>();
        foreach (var project in await query.ProjectsAsync(ct))
        {
            foreach (var board in await query.BoardsAsync(project.Id, ct))
            {
                aliases[board.Id] = (project.Alias, board.Alias);
            }
        }

        return aliases;
    }

    [GeneratedRegex(@"^/docs/note/(?<id>[0-9a-fA-F-]{32,36})/?(?:#(?<slug>.+))?$")]
    private static partial Regex NoteLink();
}
