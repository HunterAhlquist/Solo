using System.Net;
using System.Net.Http.Json;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Dtos;
namespace Solo.DesignDocs.Tests;
public sealed partial class VaultTests
{
    [SkippableFact]
    public async Task Imported_markdown_is_preserved_as_a_seed_and_folders_are_reused()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var original="\uFEFF---\r\ntitle: Café\r\n---\r\n# Note\r\n";
        var url=$"{Root}/{v.Id}/import/notes";
        var response=await _writer.PostAsJsonAsync(url,new{notes=new[]{new{path="Folder/First.md",markdown=original},new{path="Folder/Second.md",markdown="Second"}}});response.EnsureSuccessStatusCode();
        var tree=await _reader.GetJson<TreeDto>($"{Root}/{v.Id}");Assert.Single(tree.Folders);Assert.Equal(2,tree.Notes.Count);
        var note=tree.Notes.Single(n=>n.Name=="First");var state=await _reader.GetJson<System.Text.Json.JsonElement>($"/api/ext/design-docs/projects/{_project.Id}/documents/{note.Id}");
        Assert.Equal(original,state.GetProperty("seedMarkdown").GetString());Assert.Empty(state.GetProperty("updates").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden,(await _reader.PostAsJsonAsync(url,new{notes=new[]{new{path="No.md",markdown="No"}}})).StatusCode);
        await _admin.PutJson<VaultDto>($"{Root}/{v.Id}",new VaultChange(v.Name,true,v.Version));
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PostAsJsonAsync(url,new{notes=new[]{new{path="No.md",markdown="No"}}})).StatusCode);
    }
    [SkippableFact]
    public async Task Import_batch_is_atomic_on_a_name_conflict()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();await NewNote(v);
        var response=await _writer.PostAsJsonAsync($"{Root}/{v.Id}/import/notes",new{notes=new[]{new{path="New folder/Fresh.md",markdown="Fresh"},new{path="Note.md",markdown="Duplicate"}}});
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);var tree=await _reader.GetJson<TreeDto>($"{Root}/{v.Id}");Assert.Single(tree.Notes);Assert.Empty(tree.Folders);
    }
    [SkippableFact]
    public async Task A_member_super_admin_can_import_even_with_a_read_role()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var project=await _admin.GetJson<ProjectDto>($"/api/projects/{_project.Id}");
        var writer=project.Members.Single(m=>m.Email=="writer@example.com");var admin=project.Members.Single(m=>m.Email=="admin@example.com");
        await _admin.PutJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members/{writer.Id}",new{role="ReadWrite",isOwner=true});
        await _admin.PutJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members/{admin.Id}",new{role="Read",isOwner=false});
        (await _admin.PostAsJsonAsync($"{Root}/{v.Id}/import/notes",new{notes=new[]{new{path="Imported.md",markdown="Imported"}}})).EnsureSuccessStatusCode();
        var image=await _admin.PostAsync($"{Root}/{v.Id}/images/import?path=imported.png",new ByteArrayContent(Pixel));image.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,(await _reader.PostAsync($"{Root}/{v.Id}/images/import?path=no.png",new ByteArrayContent(Pixel))).StatusCode);
    }
}
