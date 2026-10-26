using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Contracts;
using Solo.DesignDocs.Data;
using Solo.DesignDocs.Media;
using Solo.DesignDocs.Sync;
using Solo.Extensibility;
using Solo.Shared;

namespace Solo.DesignDocs.Controllers;

[ApiController, Route("projects/{projectId:guid}/vaults/{vaultId:guid}/offline"),
 Authorize(Policy=SoloPolicies.ProjectRead), TypeFilter(typeof(DesignDocsErrors))]
public sealed class OfflineController(DesignDocsDbContext db, ISoloQuery query, ISoloLiveUpdates live):ControllerBase
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private async Task<Vault?> Lock(Guid projectId,Guid vaultId,CancellationToken ct)
    {
        await db.Database.BeginTransactionAsync(ct);
        return (await db.Vaults.FromSqlInterpolated($"SELECT *, xmin FROM ext_design_docs.\"Vaults\" WHERE \"Id\"={vaultId} AND \"ProjectId\"={projectId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
    }
    private async Task<bool> CanWrite(CancellationToken ct){var caller=await query.CallerAsync(ct);return caller.IsProjectOwner||caller.Role==ProjectRole.ReadWrite;}
    private static SketchDto Dto(Sketch s)=>new(s.Id,s.NoteId,s.Name,s.PageSize,s.Orientation,s.Pattern,s.LastEditedBy,s.LastEditedAt,s.Version);
    private static ImageDto Dto(DocImage i)=>new(i.Id,i.Path,i.ContentType,i.Width,i.Height,i.Bytes,i.Version);

    [HttpGet]
    public async Task<ActionResult<OfflineManifest>> Manifest(Guid projectId,Guid vaultId,CancellationToken ct)
    {
        var v=await Lock(projectId,vaultId,ct);if(v is null)return NotFound();
        var notes=await db.Notes.AsNoTracking().Where(n=>n.VaultId==vaultId).ToListAsync(ct);
        var sketches=await db.Sketches.AsNoTracking().Where(s=>s.VaultId==vaultId).ToListAsync(ct);
        var ids=notes.Select(n=>n.Id).Concat(sketches.Select(s=>s.Id)).ToArray();
        var clocks=await db.DocUpdates.Where(u=>ids.Contains(u.DocumentId)).GroupBy(u=>u.DocumentId).Select(g=>new{Id=g.Key,Seq=g.Max(u=>u.Seq)}).ToDictionaryAsync(x=>x.Id,x=>x.Seq.ToString(),ct);
        foreach(var id in ids)clocks.TryAdd(id,"0");
        var tree=new TreeDto(VaultsController.Dto(v),await db.Folders.Where(f=>f.VaultId==vaultId).Select(f=>new FolderDto(f.Id,f.ParentId,f.Name,f.Version)).ToListAsync(ct),notes.Select(VaultsController.Dto).ToArray());
        var images=(await db.Images.AsNoTracking().Where(i=>i.VaultId==vaultId).Select(i=>new DocImage{Id=i.Id,Path=i.Path,ContentType=i.ContentType,Width=i.Width,Height=i.Height,Bytes=i.Bytes,Version=i.Version}).ToListAsync(ct)).Select(Dto).ToArray();
        return new OfflineManifest(tree,sketches.Select(Dto).ToArray(),images,clocks,!v.IsClosed&&await CanWrite(ct),(await query.CallerAsync(ct)).IsSuperAdmin,await CanWrite(ct));
    }
    [HttpGet("documents/{id:guid}")]
    public async Task<ActionResult<OfflineDocument>> Read(Guid projectId,Guid vaultId,Guid id,[FromQuery]string after="0",CancellationToken ct=default)
    {
        var v=await Lock(projectId,vaultId,ct);if(v is null)return NotFound();
        var note=await db.Notes.AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&n.VaultId==vaultId,ct);
        if(note is null&&!await db.Sketches.AnyAsync(s=>s.Id==id&&s.VaultId==vaultId,ct))return NotFound();
        var rows=db.DocUpdates.AsNoTracking().Where(u=>u.DocumentId==id);var head=await rows.MaxAsync(u=>(long?)u.Seq,ct)??0;
        var checkpoint=long.TryParse(after,out var n)&&n>=0&&n<=head?n:0;
        return new OfflineDocument(id,head.ToString(),await rows.Where(u=>u.Seq>checkpoint).OrderByDescending(u=>u.IsSnapshot).ThenBy(u=>u.Seq).Select(u=>u.Data).ToListAsync(ct),note?.SeedMarkdown,await rows.CountAsync(u=>!u.IsSnapshot,ct));
    }
    [HttpPost("documents/{id:guid}"),Authorize(Policy=SoloPolicies.ProjectWrite),RequestSizeLimit(17*1024*1024)]
    public async Task<IActionResult> Write(Guid projectId,Guid vaultId,Guid id,OfflineWrite r,CancellationToken ct)
    {
        if(r.Id==Guid.Empty||r.Data is not {Length:>0 and <=12000000})return BadRequest();
        var v=await Lock(projectId,vaultId,ct);if(v is null)return NotFound();if(v.IsClosed)return Conflict(new{title="Design doc is closed"});
        var note=await db.Notes.SingleOrDefaultAsync(n=>n.Id==id&&n.VaultId==vaultId,ct);
        var sketch=note is null?await db.Sketches.SingleOrDefaultAsync(s=>s.Id==id&&s.VaultId==vaultId,ct):null;
        if(note is null&&sketch is null)return NotFound();
        var caller=await query.CallerAsync(ct);
        var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{id,r},Json)));
        var receipt=await db.OfflineReceipts.FindAsync([caller.UserId,r.Id],ct);
        if(receipt is not null){
            if(receipt.ProjectId!=projectId||receipt.VaultId!=vaultId||receipt.Hash!=hash)return Conflict(new{title="Update ID already used"});
            await db.Database.CurrentTransaction!.CommitAsync(ct);
            if(sketch is not null)await SketchPreviewChanges.PublishAsync(live,projectId,sketch,ct);
            return Content(receipt.Result,"application/json");
        }
        var previousSeq=await db.DocUpdates.Where(u=>u.DocumentId==id).MaxAsync(u=>(long?)u.Seq,ct)??0;
        var previousVersion=sketch?.Version;
        if(note?.SeedMarkdown is {} seed){
            if(r.SeedMarkdown!=seed)return Conflict(new{title="Download the current note before editing"});
            note.SeedMarkdown=null;
        }else if(r.SeedMarkdown is not null)return Conflict(new{title="Note already initialized"});
        var row=new DocUpdate{DocumentId=id,NoteId=note?.Id,SketchId=sketch?.Id,ClientUpdateId=r.Id,Data=r.Data,UserId=caller.UserId};db.Add(row);
        if(sketch is not null){sketch.LastEditedBy=caller.UserId;sketch.LastEditedAt=DateTimeOffset.UtcNow;}
        await db.SaveChangesAsync(ct);
        var result=JsonSerializer.Serialize(new OfflineWriteResult(row.Seq.ToString(),previousSeq.ToString(),previousVersion,sketch?.Version),Json);
        db.Add(new OfflineReceipt{Id=r.Id,UserId=caller.UserId,ProjectId=projectId,VaultId=vaultId,Hash=hash,Result=result});
        await db.SaveChangesAsync(ct);await db.Database.CurrentTransaction!.CommitAsync(ct);
        if(sketch is not null)await SketchPreviewChanges.PublishAsync(live,projectId,sketch,ct);
        var encoded=Convert.ToBase64String(r.Data);var count=(encoded.Length+DocProtocol.ChunkLength-1)/DocProtocol.ChunkLength;
        for(var i=0;i<count;i++)await live.PublishAsync(projectId,DocTopics.Doc(id),"update",DocProtocol.Serialize(new{seq=row.Seq.ToString(),index=i,count,data=encoded.Substring(i*DocProtocol.ChunkLength,Math.Min(DocProtocol.ChunkLength,encoded.Length-i*DocProtocol.ChunkLength))}),ct:ct);
        return Content(result,"application/json");
    }
    [HttpPost("documents/{id:guid}/compact"),Authorize(Policy=SoloPolicies.ProjectWrite),RequestSizeLimit(17*1024*1024)]
    public async Task<IActionResult> Compact(Guid projectId,Guid vaultId,Guid id,OfflineCompaction r,CancellationToken ct)
    {
        if(!long.TryParse(r.ThroughSeq,out var through)||through<=0||r.Data is not{Length:>0 and <=12000000})return BadRequest();
        var v=await Lock(projectId,vaultId,ct);if(v is null)return NotFound();if(v.IsClosed)return Conflict();
        var note=await db.Notes.SingleOrDefaultAsync(n=>n.Id==id&&n.VaultId==vaultId,ct);
        var sketch=note is null?await db.Sketches.SingleOrDefaultAsync(s=>s.Id==id&&s.VaultId==vaultId,ct):null;
        if(note is null&&sketch is null)return NotFound();
        var rows=await db.DocUpdates.Where(u=>u.DocumentId==id).ToListAsync(ct);
        if(rows.Any(u=>u.IsSnapshot&&u.ThroughSeq>=through))return new JsonResult(true);
        if(!rows.Any(u=>u.Seq==through))return Conflict();
        db.RemoveRange(rows.Where(u=>u.Seq<=through||u.IsSnapshot&&u.ThroughSeq<=through));
        db.Add(new DocUpdate{DocumentId=id,NoteId=note?.Id,SketchId=sketch?.Id,IsSnapshot=true,ThroughSeq=through,Data=r.Data,UserId=(await query.CallerAsync(ct)).UserId});
        await db.SaveChangesAsync(ct);await db.Database.CurrentTransaction!.CommitAsync(ct);return new JsonResult(true);
    }
    [HttpPost("commands"),RequestSizeLimit(15*1024*1024)]
    public async Task<IActionResult> Command(Guid projectId,Guid vaultId,OfflineCommand r,CancellationToken ct)
    {
        if(r.Id==Guid.Empty||r.TargetId==Guid.Empty||r.Entity is not("folder" or "note" or "sketch" or "image" or "vault")||r.Action is not("create" or "update" or "delete"))return BadRequest();
        var caller=await query.CallerAsync(ct);var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(r,Json)));
        // Serializing by user also protects receipts for commands that create or delete a vault.
        await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({caller.UserId.ToString()}, 0))",ct);
        var receipt=await db.OfflineReceipts.FindAsync([caller.UserId,r.Id],ct);
        if(receipt is not null)return receipt.ProjectId==projectId&&receipt.VaultId==vaultId&&receipt.Hash==hash?Content(receipt.Result,"application/json"):Conflict(new{title="Command ID already used"});
        var vault=(await db.Vaults.FromSqlInterpolated($"SELECT *, xmin FROM ext_design_docs.\"Vaults\" WHERE \"Id\"={vaultId} AND \"ProjectId\"={projectId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if(r.Entity=="vault"){if(!caller.IsSuperAdmin)return Forbid();if(r.TargetId!=vaultId)return BadRequest();}
        else{if(!await CanWrite(ct))return Forbid();if(vault is null)return NotFound();if(vault.IsClosed)return Conflict(new{title="Design doc is closed"});}
        if(r.Action=="delete"&&r.BaseVersions is not null&&!await DeletionUnchanged(vaultId,r,ct))return Conflict(new{title="Content changed elsewhere. Review it before deleting."});
        object? result=null;Sketch? changedSketch=null;string? previousName=null;var kind="tree";var deletedDocs=new List<Guid>();
        if(r.Action!="delete"&&r.Entity!="image"&&!VaultsController.ValidName(r.Name))return BadRequest();
        if(r.Entity=="vault"){
            if(r.Action=="create"){
                if(vault is not null||await db.Vaults.AnyAsync(v=>v.Id==r.TargetId,ct))return Conflict();
                vault=new(){Id=vaultId,ProjectId=projectId,Name=await Name("vault",r.Name!,null,projectId,ct),CreatedBy=caller.UserId};db.Add(vault);
            }else{
                if(vault is null)return NotFound();if(vault.Version!=r.Version)return Conflict(new{title="Design doc changed elsewhere"});
                if(r.Action=="delete"){if(!vault.IsClosed)return Conflict(new{title="Close the design doc before deleting it"});deletedDocs=await DocumentIds(vaultId,ct);db.Remove(vault);kind="deleted";}
                else{vault.Name=r.Name!.Trim();vault.IsClosed=r.IsClosed;vault.ClosedAt=r.IsClosed?DateTimeOffset.UtcNow:null;kind=r.IsClosed?"closed":"reopened";}
            }
        }else if(r.Entity is "folder" or "note"){
            if(r.ParentId is {} parent&&!await db.Folders.AnyAsync(f=>f.Id==parent&&f.VaultId==vaultId,ct))return Conflict(new{title="The destination folder no longer exists"});
            if(r.Entity=="folder"){
                var f=await db.Folders.SingleOrDefaultAsync(f=>f.Id==r.TargetId&&f.VaultId==vaultId,ct);
                if(r.Action=="create"){if(f is not null||await db.Folders.AnyAsync(x=>x.Id==r.TargetId,ct))return Conflict();f=new(){Id=r.TargetId,VaultId=vaultId,ParentId=r.ParentId,Name=await Name("folder",r.Name!,r.ParentId,vaultId,ct)};db.Add(f);}
                else{
                    if(f is null)return NotFound();if(f.Version!=r.Version)return Conflict(new{title="Folder changed elsewhere"});
                    if(r.Action=="delete"){deletedDocs=await DocumentIds(vaultId,ct);db.Remove(f);}
                    else{
                        var parents=await db.Folders.Where(x=>x.VaultId==vaultId).ToDictionaryAsync(x=>x.Id,x=>x.ParentId,ct);
                        for(var p=r.ParentId;p.HasValue;p=parents[p.Value])if(p==f.Id)return Conflict(new{title="A folder cannot contain itself"});
                        f.Name=r.Name!.Trim();f.ParentId=r.ParentId;
                    }
                }
                await db.SaveChangesAsync(ct);if(r.Action!="delete")result=new FolderDto(f.Id,f.ParentId,f.Name,f.Version);
            }else{
                var n=await db.Notes.SingleOrDefaultAsync(n=>n.Id==r.TargetId&&n.VaultId==vaultId,ct);
                if(r.Action=="create"){if(n is not null||await db.Notes.AnyAsync(x=>x.Id==r.TargetId,ct))return Conflict();n=new(){Id=r.TargetId,VaultId=vaultId,FolderId=r.ParentId,Name=await Name("note",r.Name!,r.ParentId,vaultId,ct)};db.Add(n);}
                else{
                    if(n is null)return NotFound();if(n.Version!=r.Version&&(r.Action=="delete"||n.Name!=r.BaseName||n.FolderId!=r.BaseParentId))return Conflict(new{title="Note changed elsewhere"});
                    if(r.Action=="delete"){deletedDocs=await db.Sketches.Where(s=>s.NoteId==n.Id).Select(s=>s.Id).ToListAsync(ct);deletedDocs.Add(n.Id);db.Remove(n);}
                    else{n.Name=r.Name!.Trim();n.FolderId=r.ParentId;}
                }
                await db.SaveChangesAsync(ct);if(r.Action!="delete")result=VaultsController.Dto(n);
            }
        }else if(r.Entity=="sketch"){
            var s=await db.Sketches.SingleOrDefaultAsync(s=>s.Id==r.TargetId&&s.VaultId==vaultId,ct);
            previousName=s?.Name;
            if(r.Action=="create"){
                if(s is not null||await db.Sketches.AnyAsync(x=>x.Id==r.TargetId,ct))return Conflict();
                if(!await db.Notes.AnyAsync(n=>n.Id==r.NoteId&&n.VaultId==vaultId,ct))return Conflict(new{title="The owning note no longer exists"});
                s=new(){Id=r.TargetId,VaultId=vaultId,NoteId=r.NoteId!.Value,Name=await Name("sketch",r.Name!,r.NoteId,vaultId,ct)};db.Add(s);
            }else{
                if(s is null)return NotFound();if(s.Version!=r.Version)return Conflict(new{title="Sketch changed elsewhere"});
                if(r.Action=="delete"){db.Remove(s);deletedDocs.Add(s.Id);}
                else{if(r.PageSize is not("A3" or "A4" or "A5" or "Letter")||r.Orientation is not("portrait" or "landscape")||r.Pattern is not("empty" or "grid" or "notebook" or "dots"))return BadRequest();s.Name=r.Name!.Trim();s.PageSize=r.PageSize;s.Orientation=r.Orientation;s.Pattern=r.Pattern;}
            }
            changedSketch=s;await db.SaveChangesAsync(ct);if(r.Action!="delete")result=Dto(s);
        }else{
            var image=await db.Images.SingleOrDefaultAsync(i=>i.Id==r.TargetId&&i.VaultId==vaultId,ct);
            if(r.Action=="create"){
                if(image is not null||await db.Images.AnyAsync(i=>i.Id==r.TargetId,ct))return Conflict();
                string path;
                if(r.NoteId is {} owner){
                    if(!VaultsController.ValidName(r.Name)||!await db.Notes.AnyAsync(n=>n.Id==owner&&n.VaultId==vaultId,ct))return BadRequest();
                    path=AttachmentPaths.ForNote(owner,r.Name!);
                }else{
                    if(!caller.IsSuperAdmin)return Forbid();
                    if(string.IsNullOrWhiteSpace(r.Name)||r.Name.Length>1024||r.Name.StartsWith('/')||r.Name.Contains('\\')||r.Name.Split('/').Any(p=>p is "" or "." or ".."||p.Any(char.IsControl)))return BadRequest();
                    path=r.Name;
                }
                image=new(){Id=r.TargetId,VaultId=vaultId,Path=path};
                if(await db.Images.AnyAsync(i=>i.VaultId==vaultId&&i.Path==image.Path,ct)){var slash=path.LastIndexOf('/');image.Path=path[..(slash+1)]+r.TargetId.ToString("N")+"-"+path[(slash+1)..];}
                db.Add(image);
            }else{if(image is null)return NotFound();if(image.Version!=r.Version)return Conflict(new{title="Image changed elsewhere"});}
            if(r.Action=="delete")db.Remove(image);
            else{
                if(r.Data is not{Length:>0 and <=10485760})return BadRequest();var header=ImageHeader.Read(r.Data);if(header is null)return BadRequest();
                image.Data=r.Data;image.Bytes=r.Data.Length;image.ContentType=header.ContentType;image.Width=header.Width;image.Height=header.Height;image.UploadedBy=caller.UserId;image.UploadedAt=DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);if(r.Action!="delete")result=Dto(image);kind="image";
        }
        await db.SaveChangesAsync(ct);if(r.Entity=="vault"&&r.Action!="delete")result=VaultsController.Dto(vault!);
        var json=JsonSerializer.Serialize(result,Json);db.Add(new OfflineReceipt{Id=r.Id,UserId=caller.UserId,ProjectId=projectId,VaultId=vaultId,Hash=hash,Result=json});
        await db.SaveChangesAsync(ct);await db.Database.CurrentTransaction!.CommitAsync(ct);
        await live.PublishAsync(projectId,DocTopics.Vault(vaultId),kind,ct:ct);
        if(changedSketch is not null)await SketchPreviewChanges.PublishAsync(live,projectId,changedSketch,ct,previousName);
        if(r.Entity=="vault")await live.PublishAsync(projectId,DocTopics.Vaults,"changed",ct:ct);
        foreach(var id in deletedDocs)if(!await db.Notes.AnyAsync(n=>n.Id==id,ct)&&!await db.Sketches.AnyAsync(s=>s.Id==id,ct))await live.PublishAsync(projectId,DocTopics.Doc(id),"deleted",ct:ct);
        return Content(json,"application/json");
    }
    private async Task<bool> DeletionUnchanged(Guid vaultId,OfflineCommand command,CancellationToken ct)
    {
        var folders=await db.Folders.Where(f=>f.VaultId==vaultId).ToListAsync(ct);
        var notes=await db.Notes.Where(n=>n.VaultId==vaultId).ToListAsync(ct);
        var sketches=await db.Sketches.Where(s=>s.VaultId==vaultId).ToListAsync(ct);
        var ids=new HashSet<Guid>();var versions=new Dictionary<Guid,uint>();
        if(command.Entity=="vault"){
            foreach(var f in folders)versions[f.Id]=f.Version;
            foreach(var image in await db.Images.Where(i=>i.VaultId==vaultId).Select(i=>new{i.Id,i.Version}).ToListAsync(ct))versions[image.Id]=image.Version;
        }else if(command.Entity=="folder"){
            ids.Add(command.TargetId);bool grew;
            do{grew=false;foreach(var f in folders)if(f.ParentId is {} p&&ids.Contains(p))grew|=ids.Add(f.Id);}while(grew);
            foreach(var f in folders.Where(f=>ids.Contains(f.Id)))versions[f.Id]=f.Version;
        }
        foreach(var n in notes.Where(n=>command.Entity=="vault"||command.Entity=="folder"&&n.FolderId is {} p&&ids.Contains(p)||command.Entity=="note"&&n.Id==command.TargetId))versions[n.Id]=n.Version;
        foreach(var s in sketches.Where(s=>versions.ContainsKey(s.NoteId)||command.Entity=="sketch"&&s.Id==command.TargetId))versions[s.Id]=s.Version;
        if(versions.Count!=command.BaseVersions!.Count||versions.Any(v=>!command.BaseVersions.TryGetValue(v.Key,out var expected)||expected!=v.Value))return false;
        var documentIds=notes.Where(n=>versions.ContainsKey(n.Id)).Select(n=>n.Id).Concat(sketches.Where(s=>versions.ContainsKey(s.Id)).Select(s=>s.Id)).ToArray();
        var clocks=await db.DocUpdates.Where(u=>documentIds.Contains(u.DocumentId)).GroupBy(u=>u.DocumentId).Select(g=>new{Id=g.Key,Seq=g.Max(u=>u.Seq)}).ToDictionaryAsync(x=>x.Id,x=>x.Seq.ToString(),ct);
        return command.BaseDocuments is not null&&documentIds.All(id=>command.BaseDocuments.TryGetValue(id,out var expected)&&expected==clocks.GetValueOrDefault(id,"0"));
    }
    private async Task<List<Guid>> DocumentIds(Guid vaultId,CancellationToken ct)=>await db.Notes.Where(n=>n.VaultId==vaultId).Select(n=>n.Id).Concat(db.Sketches.Where(s=>s.VaultId==vaultId).Select(s=>s.Id)).ToListAsync(ct);
    private async Task<string> Name(string kind,string proposed,Guid? parent,Guid scope,CancellationToken ct)
    {
        var name=proposed.Trim();var stem=name.Length>175?name[..175]:name;
        async Task<bool> Exists()=>kind switch{
            "vault"=>await db.Vaults.AnyAsync(v=>v.ProjectId==scope&&v.Name==name,ct),
            "folder"=>await db.Folders.AnyAsync(f=>f.VaultId==scope&&f.ParentId==parent&&f.Name==name,ct),
            "note"=>await db.Notes.AnyAsync(n=>n.VaultId==scope&&n.FolderId==parent&&n.Name==name,ct),
            _=>await db.Sketches.AnyAsync(s=>s.NoteId==parent&&s.Name==name,ct)};
        for(var i=2;await Exists();i++)name=$"{stem} (conflict {i})";
        return name;
    }
}
