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
    public void Sidebar_refreshes_after_navigation_without_a_remote_echo() {
        VaultListDto list=new(false,[]);_http.Route("vaults",HttpStatusCode.OK,()=>list);
        var cut=Render<VaultList>(p=>p.Add(x=>x.Project,_project).Add(x=>x.Compact,true));
        cut.WaitForAssertion(()=>Assert.Contains("No open design docs",cut.Markup));
        list=new(false,[new(Guid.NewGuid(),"Combat design",false,null,1)]);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/docs/DOCS");
        cut.WaitForAssertion(()=>Assert.Contains("Combat design",cut.Markup));
        Assert.DoesNotContain("No open design docs",cut.Markup);
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
    [Fact]
    public void Only_folders_have_disclosure_carets() {
        var cut=Render<DocTreeBranch>(p=>p.Add(x=>x.Entries,new List<DocTreeEntry>{
            new(Guid.NewGuid(),true,null,"Systems",1),new(Guid.NewGuid(),false,null,"Overview",1)
        }));
        Assert.Single(cut.FindAll("button[aria-label='Expand']"));
        Assert.Contains("Overview",cut.Markup);
    }

    [Fact]
    public void Folder_expansion_is_controlled_by_the_restored_preference() {
        var folder=Guid.NewGuid();
        var cut=Render<DocTreeBranch>(p=>p.Add(x=>x.Entries,new List<DocTreeEntry>{
            new(folder,true,null,"Systems",1),new(Guid.NewGuid(),false,folder,"Child note",1)
        }).Add(x=>x.OpenFolders,new HashSet<Guid>{folder}));
        Assert.Single(cut.FindAll("button[aria-label='Collapse']"));
        Assert.Contains("Child note",cut.Markup);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void Only_writers_can_drag_from_a_title_without_a_separate_handle(bool canWrite) {
        var cut=Render<DocTreeBranch>(p=>p.Add(x=>x.Entries,new List<DocTreeEntry>{new(Guid.NewGuid(),false,null,"Overview",1)}).Add(x=>x.CanWrite,canWrite));
        Assert.Equal(canWrite,cut.Find(".design-docs-tree-name").HasAttribute("data-doc-drag"));
        Assert.Empty(cut.FindAll(".design-docs-tree-handle"));
    }

    [Fact]
    public void Collapsed_children_do_not_contribute_to_the_sidebar_width() {
        var folder=Guid.NewGuid();
        var cut=Render<DocTreeBranch>(p=>p.Add(x=>x.Entries,new List<DocTreeEntry>{
            new(folder,true,null,"Systems",1),new(Guid.NewGuid(),false,folder,"A very long hidden title",1)
        }));
        Assert.Single(cut.FindAll("button[aria-label='Expand']"));
        Assert.DoesNotContain("A very long hidden title",cut.Markup);
    }

    [Fact]
    public void The_open_note_is_marked_current_without_highlighting_its_folder() {
        var folder=Guid.NewGuid();var note=Guid.NewGuid();
        var cut=Render<DocTreeBranch>(p=>p.Add(x=>x.Entries,new List<DocTreeEntry>{new(folder,true,null,"Folder",1),new(note,false,folder,"Current note",1)})
            .Add(x=>x.OpenFolders,new HashSet<Guid>{folder}).Add(x=>x.SelectedNoteId,note));
        var current=Assert.Single(cut.FindAll("button[aria-current=page]"));Assert.Equal("Current note",current.GetAttribute("title"));
        Assert.Single(cut.FindAll(".design-docs-tree-row.is-current"));
    }
}
