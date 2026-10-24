using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.DesignDocs.Media;
using Solo.Extensibility;
namespace Solo.DesignDocs.Controllers;

[ApiController,Route("projects/{projectId:guid}/vaults/{vaultId:guid}/images"),TypeFilter(typeof(DesignDocsErrors))]
public sealed class ImagesController(DesignDocsDbContext db,ISoloQuery query,ISoloLiveUpdates live):ControllerBase
{
    private const int Limit=10*1024*1024;
    private static ImageDto Dto(DocImage i)=>new(i.Id,i.Path,i.ContentType,i.Width,i.Height,i.Bytes,i.Version);
    [HttpGet,Authorize(Policy=SoloPolicies.ProjectRead)]
    public async Task<ActionResult<IReadOnlyList<ImageDto>>> List(Guid projectId,Guid vaultId,CancellationToken ct)
    {
        if(!await db.Vaults.AnyAsync(v=>v.Id==vaultId&&v.ProjectId==projectId,ct))return NotFound();
        return await db.Images.AsNoTracking().Where(i=>i.VaultId==vaultId).OrderBy(i=>i.Path)
            .Select(i=>new ImageDto(i.Id,i.Path,i.ContentType,i.Width,i.Height,i.Bytes,i.Version)).ToListAsync(ct);
    }
    [HttpGet("{id:guid}"),Authorize]
    public async Task<IActionResult> Get(Guid projectId,Guid vaultId,Guid id,CancellationToken ct)
    {
        // An image link doesn't disclose whether a nonmember's guessed id exists.
        if(!(await query.ProjectsAsync(ct)).Any(p=>p.Id==projectId))return NotFound();
        var image=await (from i in db.Images join v in db.Vaults on i.VaultId equals v.Id
            where i.Id==id&&i.VaultId==vaultId&&v.ProjectId==projectId select i).AsNoTracking().SingleOrDefaultAsync(ct);
        if(image is null)return NotFound();
        Response.Headers.ContentSecurityPolicy="default-src 'none'; style-src 'unsafe-inline'; sandbox";
        Response.Headers.XContentTypeOptions="nosniff";Response.Headers.CacheControl="no-store";
        return File(image.Data,image.ContentType);
    }
    [HttpPost,Authorize(Policy=SoloPolicies.ProjectWrite),RequestSizeLimit(Limit)]
    public Task<ActionResult<ImageDto>> Upload(Guid projectId,Guid vaultId,[FromQuery]string path,CancellationToken ct)=>Store(projectId,vaultId,null,path,null,ct);
    [HttpPost("notes/{noteId:guid}"),Authorize(Policy=SoloPolicies.ProjectWrite),RequestSizeLimit(Limit)]
    public async Task<ActionResult<ImageDto>> UploadToNote(Guid projectId,Guid vaultId,Guid noteId,[FromQuery]string path,CancellationToken ct)
    {
        if(!VaultsController.ValidName(path))return BadRequest();
        return await Store(projectId,vaultId,null,path.Trim(),null,ct,noteId);
    }
    [HttpPost("import"),Authorize(Policy=SoloPolicies.ProjectRead),RequestSizeLimit(Limit)]
    public async Task<ActionResult<ImageDto>> Import(Guid projectId,Guid vaultId,[FromQuery]string path,CancellationToken ct)
    {
        if (!(await query.CallerAsync(ct)).IsSuperAdmin) return Forbid();
        return await Store(projectId,vaultId,null,path,null,ct);
    }
    [HttpPut("{id:guid}"),Authorize(Policy=SoloPolicies.ProjectWrite),RequestSizeLimit(Limit)]
    public Task<ActionResult<ImageDto>> Replace(Guid projectId,Guid vaultId,Guid id,[FromQuery]uint version,CancellationToken ct)=>Store(projectId,vaultId,id,null,version,ct);
    private async Task<ActionResult<ImageDto>> Store(Guid projectId,Guid vaultId,Guid? id,string? path,uint? version,CancellationToken ct,Guid? noteId=null)
    {
        if(id is null&&(string.IsNullOrWhiteSpace(path)||path.Length>1024||path.StartsWith('/')||path.Contains('\\')||path.Split('/').Any(p=>p is "" or "." or ".."||p.Any(char.IsControl))))return BadRequest();
        await using var transaction=await db.Database.BeginTransactionAsync(ct);
        var vault=(await db.Vaults.FromSqlInterpolated($"SELECT *, xmin FROM ext_design_docs.\"Vaults\" WHERE \"Id\"={vaultId} AND \"ProjectId\"={projectId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if(vault is null)return NotFound();if(vault.IsClosed)return Problem(statusCode:409,title:"Design doc is closed");
        if(noteId is {} owner)
        {
            if(!await db.Notes.AnyAsync(n=>n.Id==owner&&n.VaultId==vaultId,ct))return NotFound();
            var original=AttachmentPaths.ForNote(owner,path!);
            path=original;
            var extension=Path.GetExtension(original);
            var stem=original[..^extension.Length];
            for(var number=2;await db.Images.AnyAsync(i=>i.VaultId==vaultId&&i.Path==path,ct);number++)path=$"{stem}-{number}{extension}";
        }
        DocImage image;
        if(id is {} existing){image=await db.Images.SingleOrDefaultAsync(i=>i.Id==existing&&i.VaultId==vaultId,ct)??null!;if(image is null)return NotFound();db.Entry(image).Property(i=>i.Version).OriginalValue=version!.Value;}
        else {image=new(){VaultId=vaultId,Path=path!};db.Add(image);}
        using var buffer=new MemoryStream();var chunk=new byte[81920];int read;
        while((read=await Request.Body.ReadAsync(chunk,ct))>0){if(buffer.Length+read>Limit)return StatusCode(413);buffer.Write(chunk,0,read);}
        var data=buffer.ToArray();var info=ImageHeader.Read(data);if(info is null)return Problem(statusCode:415,title:"Use a PNG, JPEG or WebP image no larger than 2,560 pixels");
        image.Data=data;image.ContentType=info.ContentType;image.Width=info.Width;image.Height=info.Height;image.Bytes=data.Length;
        image.UploadedBy=(await query.CallerAsync(ct)).UserId;image.UploadedAt=DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);await live.PublishAsync(projectId,DocTopics.Vault(vaultId),"image",ct:ct);return Dto(image);
    }
}
