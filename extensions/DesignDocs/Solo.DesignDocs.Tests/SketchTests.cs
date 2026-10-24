using System.Net;
using System.Net.Http.Json;
using Solo.DesignDocs.Contracts;
namespace Solo.DesignDocs.Tests;
public sealed partial class VaultTests
{
    [SkippableFact]
    public async Task Sketches_belong_to_notes_and_respect_closed_and_read_roles()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var n=await NewNote(v);var root=$"{Root}/{v.Id}/sketches";
        var sketch=await _writer.PostJson<SketchDto>(root+"/notes/"+n.Id,new NameRequest("Movement"));
        Assert.Equal(n.Id,sketch.NoteId);Assert.Single(await _reader.GetJson<List<SketchDto>>(root));
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PostAsJsonAsync(root+"/notes/"+n.Id,new NameRequest("Movement"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await _reader.PostAsJsonAsync(root+"/notes/"+n.Id,new NameRequest("No"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await _writer.PostAsJsonAsync(root+"/notes/"+Guid.NewGuid(),new NameRequest("No"))).StatusCode);
        var changed=await _writer.PutJson<SketchDto>(root+"/"+sketch.Id,new SketchChange(sketch.Name,"A5","landscape","dots",sketch.Version));Assert.Equal("dots",changed.Pattern);
        await _admin.PutJson<VaultDto>($"{Root}/{v.Id}",new VaultChange(v.Name,true,v.Version));
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PutAsJsonAsync(root+"/"+sketch.Id,new SketchChange(sketch.Name,"A4","portrait","empty",changed.Version))).StatusCode);
    }
    [SkippableFact]
    public async Task Different_notes_can_create_and_rename_sketches_to_the_same_name()
    {
        postgres.SkipUnlessDockerAvailable();
        var vault = await Vault();
        var first = await NewNote(vault);
        var second = await _writer.PostJson<NoteDto>($"{Root}/{vault.Id}/notes", new ItemChange("Other", null));
        var root = $"{Root}/{vault.Id}/sketches";
        var a = await _writer.PostJson<SketchDto>($"{root}/notes/{first.Id}", new NameRequest("Movement"));
        var b = await _writer.PostJson<SketchDto>($"{root}/notes/{second.Id}", new NameRequest("Movement"));
        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(a.Path, b.Path);
        Assert.Equal("Movement.sketch", AttachmentPaths.FileName(a.Path));
        foreach (var sketch in new[] { a, b })
        {
            var renamed = await _writer.PutJson<SketchDto>($"{root}/{sketch.Id}",
                new SketchChange("Plan", sketch.PageSize, sketch.Orientation, sketch.Pattern, sketch.Version));
            Assert.Equal("Plan", renamed.Name);
        }
        Assert.Equal(HttpStatusCode.Conflict,
            (await _writer.PostAsJsonAsync($"{root}/notes/{first.Id}", new NameRequest("Plan"))).StatusCode);
    }
}
