using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solo.DesignDocs.Contracts;

namespace Solo.DesignDocs.Tests;

public sealed partial class VaultTests
{
    private Task<JsonElement> Command(VaultDto vault,OfflineCommand command,HttpClient? client=null)=>(client??_writer).PostJson<JsonElement>($"{Root}/{vault.Id}/offline/commands",command);
    [SkippableFact]
    public async Task Offline_commands_keep_client_ids_and_receipts_across_lost_responses()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var folderId=Guid.NewGuid();var noteId=Guid.NewGuid();
        var create=new OfflineCommand(Guid.NewGuid(),"folder","create",folderId,Name:"Offline folder");
        var first=await Command(vault,create);var retry=await Command(vault,create);
        Assert.Equal(first.GetProperty("version").GetUInt32(),retry.GetProperty("version").GetUInt32());
        await Command(vault,new(Guid.NewGuid(),"note","create",noteId,Name:"Offline note",ParentId:folderId));
        var tree=await _writer.GetJson<TreeDto>($"{Root}/{vault.Id}");Assert.Equal(folderId,Assert.Single(tree.Folders).Id);Assert.Equal(noteId,Assert.Single(tree.Notes).Id);
        var changed=new OfflineCommand(Guid.NewGuid(),"note","update",noteId,tree.Notes[0].Version,"Renamed offline",null,BaseName:"Offline note",BaseParentId:folderId);
        var edited=await Command(vault,changed);await Command(vault,changed);
        await Command(vault,new(Guid.NewGuid(),"note","delete",noteId,edited.GetProperty("version").GetUInt32()));
        Assert.Empty((await _writer.GetJson<TreeDto>($"{Root}/{vault.Id}")).Notes);
        var reused=await _writer.PostAsJsonAsync($"{Root}/{vault.Id}/offline/commands",create with{Name="Different"});Assert.Equal(HttpStatusCode.Conflict,reused.StatusCode);
    }
    [SkippableFact]
    public async Task Concurrent_creates_keep_both_names_and_changed_metadata_requires_resolution()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();
        var a=await Command(vault,new(Guid.NewGuid(),"note","create",Guid.NewGuid(),Name:"Same name"));
        var b=await Command(vault,new(Guid.NewGuid(),"note","create",Guid.NewGuid(),Name:"Same name"));
        Assert.NotEqual(a.GetProperty("name").GetString(),b.GetProperty("name").GetString());
        var id=a.GetProperty("id").GetGuid();var version=a.GetProperty("version").GetUInt32();
        var collision=await _writer.PostAsJsonAsync($"{Root}/{vault.Id}/offline/commands",new OfflineCommand(Guid.NewGuid(),"note","update",id,version,b.GetProperty("name").GetString(),BaseName:"Same name"));
        Assert.Equal(HttpStatusCode.Conflict,collision.StatusCode);
        await Command(vault,new(Guid.NewGuid(),"note","update",id,version,"Remote rename",BaseName:"Same name"));
        var response=await _writer.PostAsJsonAsync($"{Root}/{vault.Id}/offline/commands",new OfflineCommand(Guid.NewGuid(),"note","update",id,version,"Offline rename",BaseName:"Same name"));
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
    }
    [SkippableFact]
    public async Task Offline_endpoints_recheck_membership_roles_vault_state_and_object_ownership()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var target=Guid.NewGuid();
        var command=new OfflineCommand(Guid.NewGuid(),"note","create",target,Name:"Forbidden");
        Assert.Equal(HttpStatusCode.Forbidden,(await _reader.PostAsJsonAsync($"{Root}/{vault.Id}/offline/commands",command)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await _writer.PostAsJsonAsync($"{Root}/{vault.Id}/offline/commands",new OfflineCommand(Guid.NewGuid(),"vault","update",vault.Id,vault.Version,"Forbidden"))).StatusCode);
        var other=await _admin.PostJson<VaultDto>(Root,new NameRequest("Other"));await Command(other,command);
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PostAsJsonAsync($"{Root}/{vault.Id}/offline/commands",command with{Id=Guid.NewGuid()})).StatusCode);
        var closed=await _admin.PutJson<VaultDto>($"{Root}/{vault.Id}",new VaultChange(vault.Name,true,vault.Version));
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PostAsJsonAsync($"{Root}/{vault.Id}/offline/commands",command with{Id=Guid.NewGuid(),TargetId=Guid.NewGuid()})).StatusCode);
        Assert.False((await _reader.GetJson<OfflineManifest>($"{Root}/{vault.Id}/offline")).CanWrite);
    }
    [SkippableFact]
    public async Task Offline_document_updates_acknowledge_retries_and_replay_only_new_history()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var note=await NewNote(vault);var url=$"{Root}/{vault.Id}/offline/documents/{note.Id}";
        var update=new OfflineWrite(Guid.NewGuid(),Convert.FromBase64String(Updates[0]));
        var acknowledgment=await _writer.PostJson<OfflineWriteResult>(url,update);Assert.Equal(acknowledgment,await _writer.PostJson<OfflineWriteResult>(url,update));var seq=acknowledgment.Seq;Assert.Equal("0",acknowledgment.PreviousSeq);
        var read=await _reader.GetJson<OfflineDocument>(url);Assert.Single(read.Updates);Assert.Equal(update.Data,read.Updates[0]);
        Assert.Empty((await _reader.GetJson<OfflineDocument>(url+"?after="+seq)).Updates);
        Assert.Equal(HttpStatusCode.Forbidden,(await _reader.PostAsJsonAsync(url,update with{Id=Guid.NewGuid()})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PostAsJsonAsync(url,update with{Data=Convert.FromBase64String(Updates[1])})).StatusCode);
        var manifest=await _reader.GetJson<OfflineManifest>($"{Root}/{vault.Id}/offline");Assert.Equal(seq,manifest.Documents[note.Id]);
    }
    [SkippableFact]
    public async Task Queued_deletions_refuse_unseen_children_and_unsummarized_document_edits()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var root=$"{Root}/{vault.Id}";
        var folder=await _writer.PostJson<FolderDto>(root+"/folders",new ItemChange("Folder",null));
        var note=await _writer.PostJson<NoteDto>(root+"/notes",new ItemChange("First",folder.Id));
        var expected=new Dictionary<Guid,uint>{{folder.Id,folder.Version},{note.Id,note.Version}};
        var clocks=new Dictionary<Guid,string>{{note.Id,"0"}};
        await _writer.PostJson<NoteDto>(root+"/notes",new ItemChange("New remote note",folder.Id));
        var response=await _writer.PostAsJsonAsync(root+"/offline/commands",new OfflineCommand(Guid.NewGuid(),"folder","delete",folder.Id,folder.Version,BaseDocuments:clocks,BaseVersions:expected));
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
        await _writer.PostJson<OfflineWriteResult>(root+$"/offline/documents/{note.Id}",new OfflineWrite(Guid.NewGuid(),Convert.FromBase64String(Updates[0])));
        response=await _writer.PostAsJsonAsync(root+"/offline/commands",new OfflineCommand(Guid.NewGuid(),"note","delete",note.Id,note.Version,BaseDocuments:clocks,BaseVersions:new Dictionary<Guid,uint>{{note.Id,note.Version}}));
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Equal(2,(await _reader.GetJson<TreeDto>(root)).Notes.Count);
    }

    [SkippableFact]
    public async Task Sketch_write_receipts_keep_the_predecessor_and_allow_an_unchanged_queued_deletion()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var note=await NewNote(vault);var root=$"{Root}/{vault.Id}";
        var sketch=await _writer.PostJson<SketchDto>(root+"/sketches/notes/"+note.Id,new NameRequest("Map"));
        var write=new OfflineWrite(Guid.NewGuid(),Convert.FromBase64String(Updates[0]));
        var url=root+"/offline/documents/"+sketch.Id;
        var acknowledgment=await _writer.PostJson<OfflineWriteResult>(url,write);
        Assert.Equal(sketch.Version,acknowledgment.PreviousVersion);Assert.Equal("0",acknowledgment.PreviousSeq);
        Assert.Equal(acknowledgment,await _writer.PostJson<OfflineWriteResult>(url,write));
        await Command(vault,new(Guid.NewGuid(),"sketch","delete",sketch.Id,acknowledgment.Version!.Value,
            BaseDocuments:new Dictionary<Guid,string>{{sketch.Id,acknowledgment.Seq}},BaseVersions:new Dictionary<Guid,uint>{{sketch.Id,acknowledgment.Version.Value}}));
        Assert.Empty(await _writer.GetJson<List<SketchDto>>(root+"/sketches"));
    }

}
