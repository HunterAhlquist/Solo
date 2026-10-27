using System.Net;
using System.Net.Http.Json;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Dtos;
namespace Solo.DesignDocs.Tests;

public sealed partial class VaultTests
{
    [SkippableFact]
    public async Task The_attachment_list_has_every_image_and_sketch_and_each_notes_saved_embeds()
    {
        postgres.SkipUnlessDockerAvailable();var vault=await Vault();var a=await NewNote(vault);
        var b=await _writer.PostJson<NoteDto>($"{Root}/{vault.Id}/notes",new ItemChange("Other",null));
        var sketch=await _writer.PostJson<SketchDto>($"{Root}/{vault.Id}/sketches/notes/{a.Id}",new NameRequest("Map"));var image=await UploadImage(vault,a);
        var markdown=$"![[{image.Path}|200]]\n`![[{sketch.Path}]]`";
        var copied=await _writer.PutAsJsonAsync($"{Root}/{vault.Id}/notes/{b.Id}/copy",new CopyRequest(markdown,[],"0"));copied.EnsureSuccessStatusCode();
        var list=await _reader.GetJson<AttachmentListDto>($"{Root}/{vault.Id}/attachments");
        Assert.Equal(image.Id,Assert.Single(list.Images).Id);Assert.Equal(sketch.Id,Assert.Single(list.Sketches).Id);
        Assert.Empty(list.Embeds[a.Id]);Assert.Equal([image.Path+"|200"],list.Embeds[b.Id]);
        // Another vault's attachments never appear.
        var second=await _admin.PostJson<VaultDto>(Root,new NameRequest("Second"));Assert.Empty((await _reader.GetJson<AttachmentListDto>($"{Root}/{second.Id}/attachments")).Images);
        var other=await _admin.PostJson<ProjectDto>("/api/projects",new{name="Elsewhere",alias="ELSE"});
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{other.Id}/members",new{email="stranger@example.com",role="Read"});
        using var stranger=_factory.As("stranger","stranger@example.com");
        Assert.True((await stranger.GetAsync($"{Root}/{vault.Id}/attachments")).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound,(await _reader.GetAsync($"{Root}/{Guid.NewGuid()}/attachments")).StatusCode);
    }
}
