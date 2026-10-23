using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.Extensibility;
namespace Solo.DesignDocs.Controllers;

[ApiController, Authorize, Route("")]
public sealed class LinksController(DesignDocsDbContext db, ISoloQuery query) : ControllerBase
{
    [HttpGet("notes/{id:guid}/locate")]
    public async Task<ActionResult<LocateDto>> Locate(Guid id, CancellationToken ct)
    {
        var row=await (from n in db.Notes join v in db.Vaults on n.VaultId equals v.Id where n.Id==id
            select new {n.VaultId,v.ProjectId}).SingleOrDefaultAsync(ct);
        if(row is null) return NotFound();
        var project=(await query.ProjectsAsync(ct)).FirstOrDefault(p=>p.Id==row.ProjectId);
        return project is null ? NotFound() : new LocateDto(project.Id,project.Alias,row.VaultId,id);
    }
    [HttpPost("links/labels")]
    public async Task<Dictionary<string,string>> Labels(LabelsRequest r, CancellationToken ct)
    {
        var projects=(await query.ProjectsAsync(ct)).Select(p=>p.Id).ToArray();
        var urls=r.Urls.Where(u=>u is not null&&u.Length<2048&&u.StartsWith("/docs/note/",StringComparison.Ordinal)).Distinct().ToList();
        var ids=urls.Select(u=>u[11..].Split('#')[0]).Select(s=>NoteAddress.TryDecode(s,out var id)?id:Guid.Empty).ToList();
        var notes=await (from n in db.Notes join v in db.Vaults on n.VaultId equals v.Id
            where ids.Contains(n.Id)&&projects.Contains(v.ProjectId) select n).ToDictionaryAsync(n=>n.Id,ct);
        var labels=new Dictionary<string,string>();
        foreach(var url in urls) {
            var parts=url[11..].Split('#',2);
            if(!NoteAddress.TryDecode(parts[0],out var id)||!notes.TryGetValue(id,out var n)) continue;
            var heading=parts.Length==2?(JsonSerializer.Deserialize<HeadingDto[]>(n.Headings)??[]).FirstOrDefault(h=>h.Slug==parts[1]):null;
            labels[url]=heading is null?n.Name:$"{n.Name} › {heading.Text}";
        }
        return labels;
    }
}

[ApiController, Route("projects/{projectId:guid}/colors"), Authorize(Policy=SoloPolicies.ProjectRead)]
public sealed class ColorsController(DesignDocsDbContext db, ISoloQuery query) : ControllerBase
{
    [HttpGet]
    public async Task<List<ColorDto>> List(Guid projectId, CancellationToken ct) =>
        await db.MemberColors.Where(c=>c.ProjectId==projectId).OrderBy(c=>c.Slot).Select(c=>new ColorDto(c.UserId,c.Slot)).ToListAsync(ct);
    [HttpPost]
    public async Task<ColorDto> Claim(Guid projectId, CancellationToken ct)
    {
        var user=(await query.CallerAsync(ct)).UserId;
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        // Serialize slot allocation across processes; the schema's unique keys
        // remain the final authority. The key contains only a public project id.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"design-docs:colors:"+projectId},0))",ct);
        var color=await db.MemberColors.FindAsync([projectId,user],ct);
        if(color is null) {
            color=new MemberColor {ProjectId=projectId,UserId=user,Slot=(await db.MemberColors.Where(c=>c.ProjectId==projectId).MaxAsync(c=>(int?)c.Slot,ct)??-1)+1};
            db.Add(color);await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);return new(color.UserId,color.Slot);
    }
}
