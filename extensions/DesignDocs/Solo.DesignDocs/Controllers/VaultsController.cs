using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.Extensibility;
namespace Solo.DesignDocs.Controllers;

[ApiController, Route("projects/{projectId:guid}/vaults"), TypeFilter(typeof(DesignDocsErrors))]
public sealed class VaultsController(DesignDocsDbContext db, ISoloQuery query, ISoloLiveUpdates live) : ControllerBase
{
    internal static VaultDto Dto(Vault v) => new(v.Id,v.Name,v.IsClosed,v.ClosedAt,v.Version);
    internal static NoteDto Dto(Note n) => new(n.Id,n.VaultId,n.FolderId,n.Name,n.Markdown,n.MarkdownSeq.ToString(),
        JsonSerializer.Deserialize<HeadingDto[]>(n.Headings) ?? [],n.LastEditedBy,n.LastEditedAt,n.Version);
    internal static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 200 &&
        name.Trim() is not "." and not ".." && !name.Any(c => char.IsControl(c) || "/\\:#[]|".Contains(c));

    [HttpGet, Authorize(Policy=SoloPolicies.ProjectRead)]
    public async Task<VaultListDto> List(Guid projectId, CancellationToken ct) =>
        new((await query.CallerAsync(ct)).IsSuperAdmin,
            (await db.Vaults.AsNoTracking().Where(v=>v.ProjectId==projectId).OrderBy(v=>v.Name).ToListAsync(ct)).Select(Dto).ToList());

    [HttpPost, Authorize(Policy=SoloPolicies.SuperAdmin), Authorize(Policy=SoloPolicies.ProjectOwner)]
    public async Task<ActionResult<VaultDto>> Create(Guid projectId, NameRequest request, CancellationToken ct)
    {
        if (!ValidName(request.Name)) return BadRequest();
        var v=new Vault { ProjectId=projectId, Name=request.Name.Trim(), CreatedBy=(await query.CallerAsync(ct)).UserId };
        db.Add(v); await db.SaveChangesAsync(ct); await live.PublishAsync(projectId,DocTopics.Vaults,"changed",ct:ct);
        return Dto(v);
    }

    // Locking the vault serializes close/delete with writes, including moves that
    // could otherwise race to create a folder cycle. No process-local lock is needed.
    private async Task<Vault?> Lock(Guid projectId, Guid vaultId, CancellationToken ct)
    {
        await db.Database.BeginTransactionAsync(ct);
        return (await db.Vaults.FromSqlInterpolated($"SELECT *, xmin FROM ext_design_docs.\"Vaults\" WHERE \"Id\"={vaultId} AND \"ProjectId\"={projectId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
    }
    private async Task Changed(Guid projectId, Guid vaultId, string kind, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        if(db.Database.CurrentTransaction is { } tx) await tx.CommitAsync(ct);
        await live.PublishAsync(projectId,DocTopics.Vault(vaultId),kind,ct:ct);
    }
    private ObjectResult Closed() => Problem(statusCode:409,title:"Design doc is closed",detail:"Reopen it before editing.");

    [HttpPut("{vaultId:guid}"), Authorize(Policy=SoloPolicies.SuperAdmin), Authorize(Policy=SoloPolicies.ProjectOwner)]
    public async Task<ActionResult<VaultDto>> Change(Guid projectId, Guid vaultId, VaultChange r, CancellationToken ct)
    {
        if(!ValidName(r.Name)) return BadRequest();
        var v=await Lock(projectId,vaultId,ct); if(v is null) return NotFound();
        var kind=v.IsClosed==r.IsClosed?"tree":r.IsClosed?"closed":"reopened";
        v.Name=r.Name.Trim(); v.IsClosed=r.IsClosed; v.ClosedAt=r.IsClosed?v.ClosedAt??DateTimeOffset.UtcNow:null;
        db.Entry(v).Property(x=>x.Version).OriginalValue=r.Version;
        await Changed(projectId,vaultId,kind,ct); await live.PublishAsync(projectId,DocTopics.Vaults,"changed",ct:ct);
        return Dto(v);
    }
    [HttpDelete("{vaultId:guid}"), Authorize(Policy=SoloPolicies.SuperAdmin), Authorize(Policy=SoloPolicies.ProjectOwner)]
    public async Task<IActionResult> Delete(Guid projectId, Guid vaultId, [FromQuery] uint version, CancellationToken ct)
    {
        var v=await Lock(projectId,vaultId,ct); if(v is null) return NotFound();
        if(!v.IsClosed) return Problem(statusCode:409,title:"Close the design doc before deleting it");
        var docs=await db.Notes.Where(n=>n.VaultId==vaultId).Select(n=>n.Id).Concat(db.Sketches.Where(s=>s.VaultId==vaultId).Select(s=>s.Id)).ToListAsync(ct);
        db.Entry(v).Property(x=>x.Version).OriginalValue=version; db.Remove(v);
        await Changed(projectId,vaultId,"deleted",ct);
        foreach(var id in docs) await live.PublishAsync(projectId,DocTopics.Doc(id),"deleted",ct:ct);
        await live.PublishAsync(projectId,DocTopics.Vaults,"changed",ct:ct); return NoContent();
    }
    [HttpGet("{vaultId:guid}"), Authorize(Policy=SoloPolicies.ProjectRead)]
    public async Task<ActionResult<TreeDto>> Tree(Guid projectId, Guid vaultId, CancellationToken ct)
    {
        var v=await db.Vaults.AsNoTracking().SingleOrDefaultAsync(v=>v.Id==vaultId&&v.ProjectId==projectId,ct);
        if(v is null) return NotFound();
        return new TreeDto(Dto(v),await db.Folders.Where(f=>f.VaultId==vaultId).OrderBy(f=>f.Name).Select(f=>new FolderDto(f.Id,f.ParentId,f.Name,f.Version)).ToListAsync(ct),
            (await db.Notes.AsNoTracking().Where(n=>n.VaultId==vaultId).OrderBy(n=>n.Name)
                .Select(n=>new Note {Id=n.Id,VaultId=n.VaultId,FolderId=n.FolderId,Name=n.Name,Headings=n.Headings,MarkdownSeq=n.MarkdownSeq,LastEditedBy=n.LastEditedBy,LastEditedAt=n.LastEditedAt,CreatedAt=n.CreatedAt,Version=n.Version}).ToListAsync(ct)).Select(Dto).ToList());
    }
    [HttpGet("{vaultId:guid}/notes/{id:guid}"), Authorize(Policy=SoloPolicies.ProjectRead)]
    public async Task<ActionResult<NoteDto>> Note(Guid projectId, Guid vaultId, Guid id, CancellationToken ct)
    {
        if (!await db.Vaults.AnyAsync(v => v.Id == vaultId && v.ProjectId == projectId, ct)) return NotFound();
        var note = await db.Notes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id && n.VaultId == vaultId, ct);
        return note is null ? NotFound() : Dto(note);
    }
    [HttpPost("{vaultId:guid}/{kind}"), Authorize(Policy=SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> AddItem(Guid projectId, Guid vaultId, string kind, ItemChange r, CancellationToken ct)
    {
        if(kind is not ("folders" or "notes") || !ValidName(r.Name)) return BadRequest();
        var v=await Lock(projectId,vaultId,ct); if(v is null) return NotFound(); if(v.IsClosed) return Closed();
        if(r.ParentId is {} p && !await db.Folders.AnyAsync(f=>f.Id==p&&f.VaultId==vaultId,ct)) return NotFound();
        object dto;
        if(kind=="folders") { var f=new Folder { VaultId=vaultId,ParentId=r.ParentId,Name=r.Name.Trim() }; db.Add(f); await db.SaveChangesAsync(ct); dto=new FolderDto(f.Id,f.ParentId,f.Name,f.Version); }
        else { var n=new Note { VaultId=vaultId,FolderId=r.ParentId,Name=r.Name.Trim() }; db.Add(n); await db.SaveChangesAsync(ct); dto=Dto(n); }
        await Changed(projectId,vaultId,"tree",ct); return Ok(dto);
    }
    [HttpPut("{vaultId:guid}/{kind}/{id:guid}"), Authorize(Policy=SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> ChangeItem(Guid projectId, Guid vaultId, string kind, Guid id, ItemChange r, CancellationToken ct)
    {
        if(kind is not ("folders" or "notes") || !ValidName(r.Name)) return BadRequest();
        var v=await Lock(projectId,vaultId,ct); if(v is null) return NotFound(); if(v.IsClosed) return Closed();
        var folders=await db.Folders.Where(f=>f.VaultId==vaultId).ToDictionaryAsync(f=>f.Id,ct);
        if(r.ParentId is {} p && !folders.ContainsKey(p)) return NotFound();
        if(kind=="folders") {
            if(!folders.TryGetValue(id,out var f)) return NotFound();
            for(var parent=r.ParentId;parent is {} current;parent=folders[current].ParentId)
                if(current==id) return Problem(statusCode:409,title:"A folder cannot contain itself");
            f.Name=r.Name.Trim(); f.ParentId=r.ParentId; db.Entry(f).Property(x=>x.Version).OriginalValue=r.Version;
        } else {
            var n=await db.Notes.SingleOrDefaultAsync(n=>n.Id==id&&n.VaultId==vaultId,ct); if(n is null) return NotFound();
            n.Name=r.Name.Trim();n.FolderId=r.ParentId;db.Entry(n).Property(x=>x.Version).OriginalValue=r.Version;
        }
        await Changed(projectId,vaultId,"tree",ct);
        if(kind=="notes") await live.PublishAsync(projectId,DocTopics.Doc(id),"renamed",ct:ct);
        return NoContent();
    }
    [HttpDelete("{vaultId:guid}/{kind}/{id:guid}"), Authorize(Policy=SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> DeleteItem(Guid projectId, Guid vaultId, string kind, Guid id, [FromQuery] uint version, CancellationToken ct)
    {
        if(kind is not ("folders" or "notes")) return BadRequest();
        var v=await Lock(projectId,vaultId,ct); if(v is null) return NotFound(); if(v.IsClosed) return Closed();
        var noteIds=new List<Guid>();
        if(kind=="folders") {
            var folders=await db.Folders.Where(f=>f.VaultId==vaultId).ToListAsync(ct);
            var f=folders.SingleOrDefault(f=>f.Id==id); if(f is null) return NotFound();
            var removed=new HashSet<Guid>{id}; bool grew;
            do { grew=false; foreach(var child in folders.Where(x=>x.ParentId.HasValue&&removed.Contains(x.ParentId.Value))) grew|=removed.Add(child.Id); } while(grew);
            noteIds=await db.Notes.Where(n=>n.VaultId==vaultId&&n.FolderId.HasValue&&removed.Contains(n.FolderId.Value)).Select(n=>n.Id).ToListAsync(ct);
            db.Entry(f).Property(x=>x.Version).OriginalValue=version;db.Remove(f);
        } else {
            var n=await db.Notes.SingleOrDefaultAsync(n=>n.Id==id&&n.VaultId==vaultId,ct);if(n is null) return NotFound();
            noteIds.Add(id); db.Entry(n).Property(x=>x.Version).OriginalValue=version; db.Remove(n);
        }
        var sketches=await db.Sketches.Where(s=>noteIds.Contains(s.NoteId)).Select(s=>s.Id).ToListAsync(ct);
        await Changed(projectId,vaultId,"tree",ct);
        foreach(var doc in noteIds.Concat(sketches)) await live.PublishAsync(projectId,DocTopics.Doc(doc),"deleted",ct:ct);
        return NoContent();
    }
    [HttpGet("{vaultId:guid}/notes/{id:guid}/rename-impact"), Authorize(Policy=SoloPolicies.ProjectRead)]
    public async Task<ActionResult<RenameImpactDto>> RenameImpact(Guid projectId, Guid vaultId, Guid id, CancellationToken ct)
    {
        if(!await db.Vaults.AnyAsync(v=>v.Id==vaultId&&v.ProjectId==projectId,ct)) return NotFound();
        var n=await db.Notes.SingleOrDefaultAsync(n=>n.Id==id&&n.VaultId==vaultId,ct);if(n is null) return NotFound();
        var candidates=await db.Notes.Where(x=>x.VaultId==vaultId&&x.Id!=id&&x.Markdown.Contains("[[")&&x.Markdown.Contains(n.Name)).Select(x=>x.Markdown).ToListAsync(ct);
        return new RenameImpactDto(candidates.Count(markdown=>System.Text.RegularExpressions.Regex.Matches(markdown,@"(?<!!)\[\[([^\]#|]+)(?:#[^\]|]*)?(?:\|[^\]]*)?\]\]")
            .Any(match=>{var target=match.Groups[1].Value;if(target.EndsWith(".md",StringComparison.OrdinalIgnoreCase))target=target[..^3];return target==n.Name||target.EndsWith("/"+n.Name,StringComparison.Ordinal);})));
    }
    [HttpPut("{vaultId:guid}/notes/{id:guid}/copy"), Authorize(Policy=SoloPolicies.ProjectWrite)]
    public async Task<IActionResult> Copy(Guid projectId, Guid vaultId, Guid id, CopyRequest r, CancellationToken ct)
    {
        if(!long.TryParse(r.Seq,out var seq)||seq<0||r.Headings is null||r.Headings.Count>10000||r.Headings.Any(h=>h.Text.Length>1000||h.Slug.Length>1000||h.Level is <1 or >6)) return BadRequest();
        var v=await Lock(projectId,vaultId,ct); if(v is null) return NotFound();if(v.IsClosed) return Closed();
        var n=await db.Notes.SingleOrDefaultAsync(n=>n.Id==id&&n.VaultId==vaultId,ct);if(n is null) return NotFound();
        var maximum=await db.DocUpdates.Where(u=>u.DocumentId==id).MaxAsync(u=>(long?)u.Seq,ct)??0;
        if(seq>maximum) return BadRequest();
        if(seq<n.MarkdownSeq) return NoContent();
        n.Markdown=r.Markdown;n.Headings=JsonSerializer.Serialize(r.Headings);n.MarkdownSeq=seq;
        n.LastEditedBy=(await query.CallerAsync(ct)).UserId;n.LastEditedAt=DateTimeOffset.UtcNow;
        await Changed(projectId,vaultId,"tree",ct);await live.PublishAsync(projectId,DocTopics.Doc(id),"metadata",ct:ct);return NoContent();
    }
}
