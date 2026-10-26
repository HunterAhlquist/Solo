using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.DesignDocs.Sync;
using Solo.Extensibility;
namespace Solo.DesignDocs.Controllers;

[ApiController,Route("projects/{projectId:guid}/vaults/{vaultId:guid}/sketches"),TypeFilter(typeof(DesignDocsErrors))]
public sealed class SketchesController(DesignDocsDbContext db,ISoloQuery query,ISoloLiveUpdates live):ControllerBase
{
    private static SketchDto Dto(Sketch s)=>new(s.Id,s.NoteId,s.Name,s.PageSize,s.Orientation,s.Pattern,s.LastEditedBy,s.LastEditedAt,s.Version);
    [HttpGet,Authorize(Policy=SoloPolicies.ProjectRead)]
    public async Task<ActionResult<IReadOnlyList<SketchDto>>> List(Guid projectId,Guid vaultId,CancellationToken ct)
    {
        if(!await db.Vaults.AnyAsync(v=>v.Id==vaultId&&v.ProjectId==projectId,ct))return NotFound();
        return (await db.Sketches.AsNoTracking().Where(s=>s.VaultId==vaultId).OrderBy(s=>s.Name).ToListAsync(ct)).Select(Dto).ToList();
    }
    [HttpPost("notes/{noteId:guid}"),Authorize(Policy=SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<SketchDto>> Create(Guid projectId,Guid vaultId,Guid noteId,NameRequest r,CancellationToken ct)
    {
        if(!VaultsController.ValidName(r.Name))return BadRequest();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var vault=(await db.Vaults.FromSqlInterpolated($"SELECT *, xmin FROM ext_design_docs.\"Vaults\" WHERE \"Id\"={vaultId} AND \"ProjectId\"={projectId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if(vault is null||!await db.Notes.AnyAsync(n=>n.Id==noteId&&n.VaultId==vaultId,ct))return NotFound();
        if(vault.IsClosed)return Problem(statusCode:409,title:"Design doc is closed");
        var s=new Sketch {NoteId=noteId,VaultId=vaultId,Name=r.Name.Trim()};db.Add(s);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        await live.PublishAsync(projectId,DocTopics.Vault(vaultId),"tree",ct:ct);return Dto(s);
    }
    [HttpPut("{id:guid}"),Authorize(Policy=SoloPolicies.ProjectWrite)]
    public async Task<ActionResult<SketchDto>> Change(Guid projectId,Guid vaultId,Guid id,SketchChange r,CancellationToken ct)
    {
        if(!VaultsController.ValidName(r.Name)||r.PageSize is not("A3" or "A4" or "A5" or "Letter")||r.Orientation is not("portrait" or "landscape")||r.Pattern is not("empty" or "grid" or "notebook" or "dots"))return BadRequest();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var vault=(await db.Vaults.FromSqlInterpolated($"SELECT *, xmin FROM ext_design_docs.\"Vaults\" WHERE \"Id\"={vaultId} AND \"ProjectId\"={projectId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if(vault is null)return NotFound();if(vault.IsClosed)return Problem(statusCode:409,title:"Design doc is closed");
        var s=await db.Sketches.SingleOrDefaultAsync(s=>s.Id==id&&s.VaultId==vaultId,ct);if(s is null)return NotFound();
        var previousName=s.Name;s.Name=r.Name.Trim();s.PageSize=r.PageSize;s.Orientation=r.Orientation;s.Pattern=r.Pattern;
        s.LastEditedBy=(await query.CallerAsync(ct)).UserId;s.LastEditedAt=DateTimeOffset.UtcNow;db.Entry(s).Property(x=>x.Version).OriginalValue=r.Version;
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);await SketchPreviewChanges.PublishAsync(live,projectId,s,ct,previousName);await live.PublishAsync(projectId,DocTopics.Vault(vaultId),"tree",ct:ct);return Dto(s);
    }
}
