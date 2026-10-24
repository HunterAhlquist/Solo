using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Solo.DesignDocs.Data;
using Solo.Extensibility;
using Solo.Shared;
namespace Solo.DesignDocs.Sync;

public sealed class DocumentRelay(DesignDocsDbContext db,RelayState memory) : ISoloLiveMessageHandler
{
    public async ValueTask HandleAsync(LiveMessage m,ILiveMessageReply reply,CancellationToken ct)
    {
        const string prefix="design-docs:doc:";
        if(!m.Topic.StartsWith(prefix,StringComparison.Ordinal)||!Guid.TryParseExact(m.Topic[prefix.Length..],"N",out var id))return;
        DocProtocol.Request? r;
        try {r=JsonSerializer.Deserialize<DocProtocol.Request>(m.Data??"{}",DocProtocol.Json);}catch(JsonException){return;}
        if(r is null)return;
        var vaultId=await db.Notes.Where(n=>n.Id==id).Select(n=>(Guid?)n.VaultId).SingleOrDefaultAsync(ct)
            ??await db.Sketches.Where(s=>s.Id==id).Select(s=>(Guid?)s.VaultId).SingleOrDefaultAsync(ct);
        if(vaultId is null)return;
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var vault=(await db.Vaults.FromSqlInterpolated($"SELECT *, xmin FROM ext_design_docs.\"Vaults\" WHERE \"Id\"={vaultId.Value} AND \"ProjectId\"={m.ProjectId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if(vault is null)return;
        var note=await db.Notes.SingleOrDefaultAsync(n=>n.Id==id&&n.VaultId==vault.Id,ct);
        var sketch=note is null?await db.Sketches.SingleOrDefaultAsync(s=>s.Id==id&&s.VaultId==vault.Id,ct):null;
        if(note is null&&sketch is null)return;
        var writer=!vault.IsClosed&&(m.Sender.IsProjectOwner||m.Sender.Role==ProjectRole.ReadWrite);
        async Task Refuse(string reason)=>await reply.ToSenderAsync("refused",DocProtocol.Serialize(new {r.Id,reason}),ct);
        if(m.Kind=="awareness") {
            await tx.CommitAsync(ct);
            if(writer&&r.Data is {Length:<=32000})await reply.ToOthersAsync("awareness",DocProtocol.Serialize(new {data=r.Data,userId=m.Sender.UserId}),ct);
            return;
        }
        if(m.Kind=="sync") {
            var rows=await db.DocUpdates.AsNoTracking().Where(u=>u.DocumentId==id).OrderBy(u=>u.Seq).ToListAsync(ct);
            if(note?.SeedMarkdown is {} seed&&rows.Count==0) {
                if(!writer){await reply.ToSenderAsync("unseeded",DocProtocol.Serialize(new {markdown=seed}),ct);return;}
                var lease=memory.Seed(id,m.Sender.ConnectionId);
                await reply.ToSenderAsync(lease.Connection==m.Sender.ConnectionId?"seed":"seeding",
                    DocProtocol.Serialize(new {markdown=lease.Connection==m.Sender.ConnectionId?seed:null,seedToken=lease.Token}),ct);return;
            }
            await tx.CommitAsync(ct);
            // Snapshot identities are newer than the tail they precede.
            foreach(var row in rows.OrderByDescending(u=>u.IsSnapshot).ThenBy(u=>u.Seq))
                await Chunks(reply,"state",row.Seq,row.Data,ct);
            await reply.ToSenderAsync("synced",DocProtocol.Serialize(new {seq=(rows.Count==0?0:rows.Max(u=>u.Seq)).ToString(),updates=rows.Count(u=>!u.IsSnapshot),canWrite=writer}),ct);return;
        }
        if(!writer){await Refuse(vault.IsClosed?"closed":"read-only");return;}
        if(m.Kind is not ("update" or "update-part" or "compact" or "compact-part"))return;
        if(m.Kind is not ("compact" or "compact-part")&&r.Id==Guid.Empty){await Refuse("invalid-id");return;}
        string? encoded=r.Data;
        try {
            if(m.Kind is "update-part" or "compact-part")encoded=memory.Part(m.Sender.ConnectionId,id,r.Id,r.Index,r.Count,r.Data??"");
            if(encoded is null)return;
            if(encoded.Length>16000000)throw new FormatException();
            var data=Convert.FromBase64String(encoded);if(data.Length==0)throw new FormatException();
            if(m.Kind is "compact" or "compact-part") {
                if(!long.TryParse(r.UptoSeq,out var upto)||upto<=0)return;
                var rows=await db.DocUpdates.Where(u=>u.DocumentId==id).ToListAsync(ct);
                var current=rows.Where(u=>u.IsSnapshot).Max(u=>(long?)u.ThroughSeq)??0;
                if(upto<=current||!rows.Any(u=>u.Seq==upto))return;
                db.RemoveRange(rows.Where(u=>u.Seq<=upto || u.IsSnapshot && u.ThroughSeq<=upto));
                db.Add(new DocUpdate {DocumentId=id,NoteId=note?.Id,SketchId=sketch?.Id,IsSnapshot=true,ThroughSeq=upto,Data=data,UserId=m.Sender.UserId});
                await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return;
            }
            if(r.SeedToken is {} token&&note?.SeededByToken==token) {
                var seq=await db.DocUpdates.Where(u=>u.DocumentId==id).MaxAsync(u=>(long?)u.Seq,ct)??0;
                await reply.ToSenderAsync("ack",DocProtocol.Serialize(new {r.Id,seq=seq.ToString()}),ct);return;
            }
            if(r.SeedToken is not null&&(note?.SeedMarkdown is null||!memory.HoldsSeed(id,m.Sender.ConnectionId,r.SeedToken.Value))) {await Refuse("seed-expired");return;}
            if(note?.SeedMarkdown is not null&&(r.SeedToken is null||!memory.HoldsSeed(id,m.Sender.ConnectionId,r.SeedToken.Value))) {await Refuse("seeding");return;}
            var update=new DocUpdate {DocumentId=id,NoteId=note?.Id,SketchId=sketch?.Id,Data=data,UserId=m.Sender.UserId};db.Add(update);
            if(note?.SeedMarkdown is not null){note.SeedMarkdown=null;note.SeededByToken=r.SeedToken;}
            if(sketch is not null){sketch.LastEditedBy=m.Sender.UserId;sketch.LastEditedAt=DateTimeOffset.UtcNow;}
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);memory.Seeded(id);
            await reply.ToSenderAsync("ack",DocProtocol.Serialize(new {r.Id,seq=update.Seq.ToString()}),ct);
            await Chunks(reply,"update",update.Seq,data,ct,others:true);
        }catch(FormatException){await Refuse("invalid-payload");}
    }
    private static async Task Chunks(ILiveMessageReply reply,string kind,long seq,byte[] bytes,CancellationToken ct,bool others=false)
    {
        var data=Convert.ToBase64String(bytes);var count=(data.Length+DocProtocol.ChunkLength-1)/DocProtocol.ChunkLength;
        for(var i=0;i<count;i++) {
            var json=DocProtocol.Serialize(new {seq=seq.ToString(),index=i,count,data=data.Substring(i*DocProtocol.ChunkLength,Math.Min(DocProtocol.ChunkLength,data.Length-i*DocProtocol.ChunkLength))});
            if(others)await reply.ToOthersAsync(kind,json,ct);else await reply.ToSenderAsync(kind,json,ct);
        }
    }
}
