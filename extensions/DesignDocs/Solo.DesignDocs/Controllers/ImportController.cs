using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Data;
using Solo.DesignDocs.Contracts;
using Solo.Extensibility;
using Solo.Shared;
namespace Solo.DesignDocs.Controllers;

[ApiController,Route("projects/{projectId:guid}/vaults/{vaultId:guid}/import"),Authorize(Policy=SoloPolicies.ProjectRead),TypeFilter(typeof(DesignDocsErrors))]
public sealed class ImportController(DesignDocsDbContext db,ISoloQuery query,ISoloLiveUpdates live):ControllerBase
{
    public sealed record ImportedNote([Required]string Path,[Required(AllowEmptyStrings=true)]string Markdown);
    public sealed record Batch([Required,MaxLength(100)]IReadOnlyList<ImportedNote> Notes);
    [HttpPost("notes"),RequestSizeLimit(5*1024*1024)]
    public async Task<IActionResult> Notes(Guid projectId,Guid vaultId,Batch request,CancellationToken ct)
    {
        var caller=await query.CallerAsync(ct);
        if(!caller.IsSuperAdmin&&!caller.IsProjectOwner&&caller.Role!=ProjectRole.ReadWrite)return Forbid();
        if(request.Notes.Any(n=>Encoding.UTF8.GetByteCount(n.Markdown)>1048576||!n.Path.EndsWith(".md",StringComparison.OrdinalIgnoreCase)||n.Path.StartsWith('/')||n.Path.Contains('\\')||n.Path.Split('/')[..^1].Any(p=>!VaultsController.ValidName(p)||p!=p.Trim())||!VaultsController.ValidName(n.Path.Split('/')[^1][..^3])))return BadRequest();
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var vault=(await db.Vaults.FromSqlInterpolated($"SELECT *, xmin FROM ext_design_docs.\"Vaults\" WHERE \"Id\"={vaultId} AND \"ProjectId\"={projectId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if(vault is null)return NotFound();if(vault.IsClosed)return Problem(statusCode:409,title:"Design doc is closed");
        var folders=await db.Folders.Where(f=>f.VaultId==vaultId).ToListAsync(ct);
        foreach(var imported in request.Notes){
            var parts=imported.Path.Split('/');Guid? parent=null;
            foreach(var name in parts[..^1]){var folder=folders.FirstOrDefault(f=>f.ParentId==parent&&f.Name==name);
                if(folder is null){folder=new(){VaultId=vaultId,ParentId=parent,Name=name};folders.Add(folder);db.Add(folder);}parent=folder.Id;}
            db.Add(new Note{VaultId=vaultId,FolderId=parent,Name=parts[^1][..^3],Markdown=imported.Markdown,SeedMarkdown=imported.Markdown});
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);await live.PublishAsync(projectId,DocTopics.Vault(vaultId),"tree",ct:ct);
        return Ok(new{imported=request.Notes.Count});
    }
}
