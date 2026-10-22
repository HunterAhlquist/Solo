using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
namespace Solo.DesignDocs.Tests;

public sealed class VaultComponentTests : ComponentTestBase
{
    private readonly StubHttp _http=new();private readonly FakeLiveTopics _live=new();
    private readonly ProjectSummaryDto _project=new(Guid.NewGuid(),"Project","DOCS",null,null,ProjectRole.ReadWrite,false);
    public VaultComponentTests(){Services.AddSingleton<ISoloClientApi>(_http.CreateClient());Services.AddSingleton<ISoloLiveTopics>(_live);}
    [Theory][InlineData(false)][InlineData(true)]
    public void Only_super_admins_see_create(bool manage) {
        _http.RouteJson("vaults",new VaultListDto(manage,[]));
        var cut=Render<VaultList>(p=>p.Add(x=>x.Project,_project));
        cut.WaitForAssertion(()=>Assert.Contains("No open design docs",cut.Markup));
        Assert.Equal(manage,cut.Markup.Contains("New design doc"));
    }
    [Fact]
    public async Task Lists_follow_live_changes_and_release_their_subscription() {
        var v=new VaultDto(Guid.NewGuid(),"Movement",false,null,1);
        VaultListDto list=new(false,[v]);_http.Route("vaults",HttpStatusCode.OK,()=>list);
        var cut=Render<VaultList>(p=>p.Add(x=>x.Project,_project));
        cut.WaitForAssertion(()=>Assert.Contains("Movement",cut.Markup));
        list=new(false,[v with {Name="Combat"}]);
        await cut.InvokeAsync(()=>_live.SendAsync(_project.Id,DocTopics.Vaults,"changed"));
        cut.WaitForAssertion(()=>Assert.Contains("Combat",cut.Markup));Assert.DoesNotContain("Movement",cut.Markup);
        await DisposeComponentsOnDispatcherAsync();Assert.Empty(_live.Held);
    }
    [Fact]
    public void Closed_vaults_are_hidden_until_requested() {
        _http.RouteJson("vaults",new VaultListDto(false,[new(Guid.NewGuid(),"Archived",true,DateTimeOffset.UtcNow,1)]));
        var cut=Render<VaultList>(p=>p.Add(x=>x.Project,_project));
        cut.WaitForAssertion(()=>Assert.Contains("Show closed",cut.Markup));Assert.DoesNotContain("Archived",cut.Markup);
        cut.Find("input[type=checkbox]").Change(true);Assert.Contains("Archived",cut.Markup);
    }
    [Fact]
    public void Reader_tree_has_no_mutation_controls() {
        var id=Guid.NewGuid();
        var cut=Render<DocTreeBranch>(p=>p.Add(x=>x.Entries,new List<DocTreeEntry>{new(id,false,null,"Read me",1)}).Add(x=>x.CanWrite,false));
        Assert.Contains("Read me",cut.Markup);Assert.DoesNotContain("Actions for",cut.Markup);
    }
}
