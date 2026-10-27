using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Solo.DesignDocs.Contracts;
namespace Solo.DesignDocs.Tests;

public sealed partial class VaultTests
{
    private async Task<long> Rows(string table,string column,Guid id)
    {
        await using var connection=new NpgsqlConnection(_connectionString);await connection.OpenAsync();
        await using var command=new NpgsqlCommand($"SELECT count(*) FROM ext_design_docs.\"{table}\" WHERE \"{column}\"=@id",connection);
        command.Parameters.AddWithValue("id",id);return (long)(await command.ExecuteScalarAsync())!;
    }
    private async Task<ImageDto> UploadImage(VaultDto vault,NoteDto note)
    {
        var response=await _writer.PostAsync($"{Root}/{vault.Id}/images/notes/{note.Id}?path=plan.png",new ByteArrayContent(Pixel));response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImageDto>(TestClients.Json))!;
    }

    [SkippableFact]
    public async Task Deleting_a_sketch_needs_write_access_and_its_current_version()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var note=await NewNote(vault);var root=$"{Root}/{vault.Id}/sketches";
        var sketch=await _writer.PostJson<SketchDto>($"{root}/notes/{note.Id}",new NameRequest("Map"));
        Assert.Equal(HttpStatusCode.Forbidden,(await _reader.DeleteAsync($"{root}/{sketch.Id}?version={sketch.Version}")).StatusCode);
        var changed=await _writer.PutJson<SketchDto>($"{root}/{sketch.Id}",new SketchChange("Map","A5","landscape","grid",sketch.Version));
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.DeleteAsync($"{root}/{sketch.Id}?version={sketch.Version}")).StatusCode);
        Assert.Single(await _reader.GetJson<List<SketchDto>>(root));
        Assert.Equal(HttpStatusCode.NotFound,(await _writer.DeleteAsync($"{root}/{Guid.NewGuid()}?version=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await _writer.DeleteAsync($"{root}/{sketch.Id}?version={changed.Version}")).StatusCode);
        Assert.Empty(await _reader.GetJson<List<SketchDto>>(root));
        Assert.Equal(HttpStatusCode.NotFound,(await _writer.DeleteAsync($"{root}/{sketch.Id}?version={changed.Version}")).StatusCode);
        // The note that owned it is untouched.
        Assert.Equal(note.Id,(await _reader.GetJson<NoteDto>($"{Root}/{vault.Id}/notes/{note.Id}")).Id);
    }
    [SkippableFact]
    public async Task A_closed_vault_keeps_its_sketches_and_images()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var note=await NewNote(vault);
        var sketch=await _writer.PostJson<SketchDto>($"{Root}/{vault.Id}/sketches/notes/{note.Id}",new NameRequest("Map"));var image=await UploadImage(vault,note);
        await _admin.PutJson<VaultDto>($"{Root}/{vault.Id}",new VaultChange(vault.Name,true,vault.Version));
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.DeleteAsync($"{Root}/{vault.Id}/sketches/{sketch.Id}?version={sketch.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.DeleteAsync($"{Root}/{vault.Id}/images/{image.Id}?version={image.Version}")).StatusCode);
        Assert.Single(await _reader.GetJson<List<SketchDto>>($"{Root}/{vault.Id}/sketches"));Assert.Single(await _reader.GetJson<List<ImageDto>>($"{Root}/{vault.Id}/images"));
    }
    [SkippableFact]
    public async Task Deleting_a_sketch_removes_its_history_and_tells_open_editors_and_previews()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var note=await NewNote(vault);
        var sketch=await _writer.PostJson<SketchDto>($"{Root}/{vault.Id}/sketches/notes/{note.Id}",new NameRequest("Map"));
        await _writer.PostJson<OfflineWriteResult>($"{Root}/{vault.Id}/offline/documents/{sketch.Id}",new OfflineWrite(Guid.NewGuid(),Convert.FromBase64String(Updates[0])));
        Assert.Equal(1,await Rows("DocUpdates","SketchId",sketch.Id));
        var current=(await _writer.GetJson<List<SketchDto>>($"{Root}/{vault.Id}/sketches")).Single();
        await using var editor=await Connect(sketch.Id,"reader");await using var preview=await ConnectTopic(DocTopics.Vault(vault.Id),"reader");
        Assert.Equal(HttpStatusCode.NoContent,(await _writer.DeleteAsync($"{Root}/{vault.Id}/sketches/{sketch.Id}?version={current.Version}")).StatusCode);
        Assert.True(await editor.Pending("deleted",TimeSpan.FromSeconds(8)),"an open sketch editor goes read-only");
        var changed=await preview.Next("sketch");
        Assert.Equal(note.Id,changed.GetProperty("noteId").GetGuid());Assert.Equal(sketch.Path,changed.GetProperty("path").GetString());
        Assert.True(await preview.Pending("tree",TimeSpan.FromSeconds(8)));
        Assert.Equal(0,await Rows("DocUpdates","SketchId",sketch.Id));Assert.Equal(0,await Rows("Sketches","Id",sketch.Id));
        Assert.Equal(HttpStatusCode.NotFound,(await _reader.GetAsync($"{Root}/{vault.Id}/offline/documents/{sketch.Id}")).StatusCode);
    }
    [SkippableFact]
    public async Task Deleting_an_image_needs_write_access_and_its_current_version_and_tells_open_notes()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var note=await NewNote(vault);var root=$"{Root}/{vault.Id}/images";var image=await UploadImage(vault,note);
        Assert.Equal(HttpStatusCode.Forbidden,(await _reader.DeleteAsync($"{root}/{image.Id}?version={image.Version}")).StatusCode);
        var replaced=await _writer.PutAsync($"{root}/{image.Id}?version={image.Version}",new ByteArrayContent(Pixel));replaced.EnsureSuccessStatusCode();
        var current=(await replaced.Content.ReadFromJsonAsync<ImageDto>(TestClients.Json))!;
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.DeleteAsync($"{root}/{image.Id}?version={image.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await _writer.DeleteAsync($"{root}/{Guid.NewGuid()}?version=0")).StatusCode);
        await using var viewer=await ConnectTopic(DocTopics.Vault(vault.Id),"reader");
        Assert.Equal(HttpStatusCode.NoContent,(await _writer.DeleteAsync($"{root}/{image.Id}?version={current.Version}")).StatusCode);
        Assert.True(await viewer.Pending("image",TimeSpan.FromSeconds(8)),"open notes refresh their image previews");
        Assert.Empty(await _reader.GetJson<List<ImageDto>>(root));Assert.Equal(HttpStatusCode.NotFound,(await _reader.GetAsync($"{root}/{image.Id}")).StatusCode);
        Assert.Equal(0,await Rows("Images","Id",image.Id));
    }
    [SkippableFact]
    public async Task A_downloaded_vault_deletes_an_image_through_its_command_queue()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var note=await NewNote(vault);var image=await UploadImage(vault,note);
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PostAsJsonAsync($"{Root}/{vault.Id}/offline/commands",new OfflineCommand(Guid.NewGuid(),"image","delete",image.Id,image.Version+1))).StatusCode);
        await Command(vault,new(Guid.NewGuid(),"image","delete",image.Id,image.Version));
        Assert.Empty(await _reader.GetJson<List<ImageDto>>($"{Root}/{vault.Id}/images"));
    }
}
