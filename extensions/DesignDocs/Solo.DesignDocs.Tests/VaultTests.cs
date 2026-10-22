using System.Net;
using System.Net.Http.Json;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Dtos;
namespace Solo.DesignDocs.Tests;

[Collection("postgres")]
public sealed partial class VaultTests(PostgresFixture postgres) : IAsyncLifetime
{
    private SoloApiFactory _factory=null!;
    private string _connectionString=null!;
    private HttpClient _admin=null!, _writer=null!, _reader=null!;
    private ProjectDto _project=null!;
    private string Root => $"/api/ext/design-docs/projects/{_project.Id}/vaults";
    public async Task InitializeAsync()
    {
        if(!postgres.DockerAvailable) return;
        _connectionString=await postgres.CreateDatabaseAsync("docs");
        _factory=new(_connectionString,[SoloApiFactory.PackageDirectory("design-docs")]);
        _admin=_factory.As("admin","admin@example.com");await TestClients.CompleteSetupAsync(_admin);
        _project=await _admin.PostJson<ProjectDto>("/api/projects",new {name="Docs",alias="DOCS"});
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members",new {email="writer@example.com",role="ReadWrite"});
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members",new {email="reader@example.com",role="Read"});
        _writer=_factory.As("writer","writer@example.com");_reader=_factory.As("reader","reader@example.com");
        await _writer.GetJson<UserDto>("/api/users/me");await _reader.GetJson<UserDto>("/api/users/me");
    }
    public async Task DisposeAsync() { _admin?.Dispose();_writer?.Dispose();_reader?.Dispose();if(_factory is not null) await _factory.DisposeAsync(); }
    private Task<VaultDto> Vault() => _admin.PostJson<VaultDto>(Root,new NameRequest("Design"));

    [SkippableFact]
    public async Task Reader_sees_tree_but_cannot_write_and_writer_cannot_manage_lifecycle()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var url=$"{Root}/{v.Id}";
        var folder=await _writer.PostJson<FolderDto>(url+"/folders",new ItemChange("Mechanics",null));
        await _writer.PostJson<NoteDto>(url+"/notes",new ItemChange("Movement",folder.Id));
        var tree=await _reader.GetJson<TreeDto>(url);Assert.Single(tree.Folders);Assert.Single(tree.Notes);
        Assert.False((await _reader.GetJson<VaultListDto>(Root)).CanManage);
        Assert.Equal(HttpStatusCode.Forbidden,(await _reader.PostAsJsonAsync(url+"/notes",new ItemChange("No",null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await _writer.PostAsJsonAsync(Root,new NameRequest("No"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await _writer.PutAsJsonAsync(url,new VaultChange(v.Name,true,v.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await _writer.DeleteAsync(url+"?version="+v.Version)).StatusCode);
    }
    [SkippableFact]
    public async Task Close_requires_fresh_version_blocks_writes_and_delete_requires_closed()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var url=$"{Root}/{v.Id}";
        Assert.Equal(HttpStatusCode.Conflict,(await _admin.DeleteAsync(url+"?version="+v.Version)).StatusCode);
        var closed=await _admin.PutJson<VaultDto>(url,new VaultChange(v.Name,true,v.Version));
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PostAsJsonAsync(url+"/notes",new ItemChange("No",null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await _admin.PutAsJsonAsync(url,new VaultChange(v.Name,false,v.Version))).StatusCode);
        var reopened=await _admin.PutJson<VaultDto>(url,new VaultChange(v.Name,false,closed.Version));
        await _writer.PostJson<NoteDto>(url+"/notes",new ItemChange("Yes",null));
        closed=await _admin.PutJson<VaultDto>(url,new VaultChange(v.Name,true,reopened.Version));
        Assert.Equal(HttpStatusCode.NoContent,(await _admin.DeleteAsync(url+"?version="+closed.Version)).StatusCode);
    }
    [SkippableFact]
    public async Task Cross_project_ids_and_unreadable_links_are_not_found()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var url=$"{Root}/{v.Id}";
        var note=await _writer.PostJson<NoteDto>(url+"/notes",new ItemChange("Secret",null));
        var other=await _admin.PostJson<ProjectDto>("/api/projects",new {name="Other",alias="OTHER"});
        var wrong=$"/api/ext/design-docs/projects/{other.Id}/vaults/{v.Id}";
        Assert.Equal(HttpStatusCode.NotFound,(await _admin.GetAsync(wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await _admin.PostAsJsonAsync(wrong+"/notes",new ItemChange("No",null))).StatusCode);
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{other.Id}/members",new {email="stranger@example.com",role="Read"});
        using var stranger=_factory.As("stranger","stranger@example.com");
        Assert.Equal(HttpStatusCode.NotFound,(await stranger.GetAsync($"/api/ext/design-docs/notes/{note.Id}/locate")).StatusCode);
        Assert.Empty(await stranger.PostJson<Dictionary<string,string>>("/api/ext/design-docs/links/labels",new LabelsRequest([$"/docs/note/{note.Id:N}"])));
    }
    [SkippableFact]
    public async Task Nonmember_super_admin_can_manage_but_cannot_read_contents()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();
        var members=await _admin.GetJson<ProjectDto>($"/api/projects/{_project.Id}");
        var owner=members.Members.Single(m=>m.Email=="writer@example.com");
        await _admin.PutJson<ProjectMemberDto>($"/api/projects/{_project.Id}/members/{owner.Id}",new {role="ReadWrite",isOwner=true});
        var admin=members.Members.Single(m=>m.Email=="admin@example.com");
        (await _admin.DeleteAsync($"/api/projects/{_project.Id}/members/{admin.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,(await _admin.GetAsync($"{Root}/{v.Id}")).StatusCode);
        var closed=await _admin.PutJson<VaultDto>($"{Root}/{v.Id}",new VaultChange(v.Name,true,v.Version));
        Assert.Equal(HttpStatusCode.NoContent,(await _admin.DeleteAsync($"{Root}/{v.Id}?version={closed.Version}")).StatusCode);
        await _admin.PostJson<VaultDto>(Root,new NameRequest("Another"));
    }
    [SkippableFact]
    public async Task Folder_cycles_duplicate_root_names_and_stale_moves_are_conflicts()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var url=$"{Root}/{v.Id}";
        var a=await _writer.PostJson<FolderDto>(url+"/folders",new ItemChange("A",null));
        var b=await _writer.PostJson<FolderDto>(url+"/folders",new ItemChange("B",a.Id));
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PutAsJsonAsync(url+"/folders/"+a.Id,new ItemChange("A",b.Id,a.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PostAsJsonAsync(url+"/folders",new ItemChange("A",null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PutAsJsonAsync(url+"/folders/"+b.Id,new ItemChange("B",null,0))).StatusCode);
    }
    [SkippableFact]
    public async Task Locate_labels_and_colors_resolve_without_exposing_other_projects()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();
        var note=await _writer.PostJson<NoteDto>($"{Root}/{v.Id}/notes",new ItemChange("Movement",null));
        var located=await _reader.GetJson<LocateDto>($"/api/ext/design-docs/notes/{note.Id}/locate");Assert.Equal(_project.Id,located.ProjectId);
        var url=$"/docs/note/{note.Id:N}";
        var labels=await _reader.PostJson<Dictionary<string,string>>("/api/ext/design-docs/links/labels",new LabelsRequest([url]));Assert.Equal(note.Name,labels[url]);
        var path=$"/api/ext/design-docs/projects/{_project.Id}/colors";
        var colors=await Task.WhenAll(Enumerable.Range(0,5).Select(_=>_writer.PostJson<ColorDto>(path,new {})));
        Assert.All(colors,c=>Assert.Equal(0,c.Slot));Assert.Equal(1,(await _reader.PostJson<ColorDto>(path,new {})).Slot);
    }
}
