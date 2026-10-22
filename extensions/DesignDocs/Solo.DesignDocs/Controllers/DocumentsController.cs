using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Data;
using Solo.Extensibility;
namespace Solo.DesignDocs.Controllers;

[ApiController, Route("projects/{projectId:guid}/documents"), Authorize(Policy=SoloPolicies.ProjectRead)]
public sealed class DocumentsController(DesignDocsDbContext db) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid id, CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var note=await (from n in db.Notes join v in db.Vaults on n.VaultId equals v.Id
            where n.Id==id&&v.ProjectId==projectId select n).AsNoTracking().SingleOrDefaultAsync(ct);
        if(note is null&&!await (from s in db.Sketches join v in db.Vaults on s.VaultId equals v.Id
            where s.Id==id&&v.ProjectId==projectId select s).AnyAsync(ct)) return NotFound();
        // A consistent read keeps a concurrent compaction from hiding its tail.
        var rows=await db.DocUpdates.AsNoTracking().Where(u=>u.DocumentId==id).OrderByDescending(u=>u.IsSnapshot).ThenBy(u=>u.Seq)
            .Select(u=>new {seq=u.Seq.ToString(),u.IsSnapshot,throughSeq=u.ThroughSeq.ToString(),u.Data}).ToListAsync(ct);
        await tx.CommitAsync(ct);return Ok(new {seedMarkdown=note?.SeedMarkdown,updates=rows});
    }
}
