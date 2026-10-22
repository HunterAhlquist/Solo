using System.Net;
using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Solo.Shared;
namespace Solo.DesignDocs.Tests;

public sealed class DocLinkTests
{
    [Fact]
    public async Task Label_cache_is_scoped_to_the_signed_in_user()
    {
        var http=new StubHttp();var user=Guid.NewGuid();var label="Movement";const string url="/docs/note/00000000000000000000000000000001";
        http.Route("api/users/me",HttpStatusCode.OK,()=>new UserDto(user,"test@example.com","Test",null,false));
        http.Route("links/labels",HttpStatusCode.OK,()=>new Dictionary<string,string>{{url,label}});
        var provider=new DocLinkLabels(http.CreateClient());
        Assert.True(provider.Claims(url));Assert.False(provider.Claims("https://outside.test/docs/note/a"));
        Assert.Equal(label,(await provider.LabelsAsync([url],default))[url]);
        label="Renamed";Assert.Equal("Movement",(await provider.LabelsAsync([url],default))[url]);
        user=Guid.NewGuid();Assert.Equal("Renamed",(await provider.LabelsAsync([url],default))[url]);
    }
    [Fact]
    public async Task Link_action_is_absent_without_a_project()
    {
        var action=new DocLinkAction(null!);
        Assert.Empty(await action.ButtonsAsync(new(Guid.Empty,MarkdownSurface.TaskDescription,Guid.NewGuid(),null),default));
        Assert.Single(await action.ButtonsAsync(new(Guid.NewGuid(),MarkdownSurface.Comment,Guid.NewGuid(),null),default));
    }
}
public sealed partial class VaultTests
{
    [SkippableFact]
    public async Task Doc_labels_follow_rename_and_resolve_a_heading()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var n=await NewNote(v);var path=$"{Root}/{v.Id}/notes/{n.Id}";
        (await _writer.PutAsync(path+"/copy",System.Net.Http.Json.JsonContent.Create(new CopyRequest("# Dash",[new("dash","Dash",1)],"0")))).EnsureSuccessStatusCode();
        var current=await _writer.GetJson<NoteDto>(path);
        (await _writer.PutAsync(path,System.Net.Http.Json.JsonContent.Create(new ItemChange("Renamed",null,current.Version)))).EnsureSuccessStatusCode();
        var url=$"/docs/note/{n.Id:N}#dash";
        var labels=await _reader.PostJson<Dictionary<string,string>>("/api/ext/design-docs/links/labels",new LabelsRequest([url]));
        Assert.Equal("Renamed › Dash",labels[url]);
        Assert.Equal(n.Id,(await _reader.GetJson<LocateDto>($"/api/ext/design-docs/notes/{n.Id}/locate")).NoteId);
    }
}
