using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.State;
using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
namespace Solo.DesignDocs.Tests;

public sealed class PreferenceLifecycleTests : ComponentTestBase
{
    [Fact]
    public async Task A_rerender_during_preference_loading_cannot_save_default_values()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();var folder=Guid.NewGuid();
        var http=new StubHttp();http.RouteJson($"api/projects/{project}",new ProjectDto(project,"Project","DOCS",null,null,1,[]));
        http.RouteJson("api/projects",new[]{new ProjectSummaryDto(project,"Project","DOCS",null,null,ProjectRole.ReadWrite,true)});
        http.RouteJson("api/users/me",new UserDto(Guid.NewGuid(),"fixture@example.test","Fixture",null,true));
        http.RouteJson($"vaults/{vault}",new TreeDto(new(vault,"Vault",false,null,1),[new(folder,null,"Folder",1)],[new(note,vault,folder,"Note","","0",[],null,null,1)]));
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());Services.AddSingleton<ISoloLiveTopics>(new FakeLiveTopics());
        Services.AddScoped<ISoloWorkspace>(sp=>sp.GetRequiredService<WorkspaceState>());Services.AddScoped<ISoloContextMenu>(sp=>sp.GetRequiredService<ContextMenuService>());
        ComponentFactories.AddStub<NoteEditor>();ComponentFactories.AddStub<DocHierarchy>();
        var module=JSInterop.SetupModule(_=>true);var loading=module.Setup<VaultViewPreferences>("loadVaultPreferences",_=>true);module.SetupVoid("saveVaultPreferences",_=>true).SetVoidResult();
        var cut=Render<DesignDocsPage>(p=>p.Add(x=>x.ProjectAlias,"DOCS").Add(x=>x.VaultId,vault));
        cut.WaitForState(()=>module.Invocations.Any(i=>i.Identifier=="loadVaultPreferences"));
        try{
            await cut.InvokeAsync(()=>cut.Instance.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string,object?>{{nameof(DesignDocsPage.ProjectAlias),"DOCS"},{nameof(DesignDocsPage.VaultId),vault}})));
            Assert.DoesNotContain(module.Invocations,i=>i.Identifier=="saveVaultPreferences");
        }finally{loading.SetResult(new([folder],note,150));}
        cut.WaitForAssertion(()=>Assert.Contains("--design-docs-font-scale:150%",cut.Markup));
    }

    [Fact]
    public async Task Changing_compact_note_routes_keeps_the_workspace_and_hydrated_preferences()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var first=Guid.NewGuid();var second=Guid.NewGuid();var folder=Guid.NewGuid();
        var http=new StubHttp();
        http.RouteJson($"api/projects/{project}",new ProjectDto(project,"Project","DOCS",null,null,1,[]));
        http.RouteJson("api/projects",new[]{new ProjectSummaryDto(project,"Project","DOCS",null,null,ProjectRole.ReadWrite,true)});
        http.RouteJson("api/users/me",new UserDto(Guid.NewGuid(),"fixture@example.test","Fixture",null,true));
        http.RouteJson($"vaults/{vault}",new TreeDto(new(vault,"Vault",false,null,1),[new(folder,null,"Folder",1)],
            [new(first,vault,folder,"First","","0",[],null,null,1),new(second,vault,folder,"Second","","0",[],null,null,1)]));
        http.RouteJson("vaults",new VaultListDto(false,[new(vault,"Vault",false,null,1)]));
        http.RouteJson($"notes/{first}/locate",new LocateDto(project,"DOCS",vault,first));
        http.RouteJson($"notes/{second}/locate",new LocateDto(project,"DOCS",vault,second));
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());Services.AddSingleton<ISoloLiveTopics>(new FakeLiveTopics());
        var workspace=new RecordingWorkspace();Services.AddSingleton<ISoloWorkspace>(workspace);
        Services.AddScoped<ISoloContextMenu>(sp=>sp.GetRequiredService<ContextMenuService>());
        ComponentFactories.AddStub<NoteEditor>(parameters=>builder=>{builder.OpenElement(0,"span");builder.AddAttribute(1,"data-note",parameters.Get(x=>x.Note)!.Id);builder.CloseElement();});
        ComponentFactories.AddStub<DocHierarchy>();
        var module=JSInterop.SetupModule(_=>true);
        module.Setup<VaultViewPreferences>("loadVaultPreferences",_=>true).SetResult(new([folder],first,150,15));
        module.SetupVoid("saveVaultPreferences",_=>true).SetVoidResult();
        var cut=Render<DesignDocsPage>(p=>p.Add(x=>x.RouteNoteKey,NoteAddress.Encode(first)));
        cut.WaitForAssertion(()=>Assert.Equal(first.ToString(),cut.Find("[data-note]").GetAttribute("data-note")));
        await cut.InvokeAsync(()=>cut.Instance.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string,object?>{{nameof(DesignDocsPage.RouteNoteKey),NoteAddress.Encode(second)}})));
        cut.WaitForAssertion(()=>Assert.Equal(second.ToString(),cut.Find("[data-note]").GetAttribute("data-note")));
        Assert.Equal(1,workspace.Entered);Assert.Equal(0,workspace.Disposed);
        Assert.Contains("--design-docs-font-scale:150%",cut.Markup);Assert.Contains("--design-docs-margin:15cqw",cut.Markup);
        Assert.Single(module.Invocations,i=>i.Identifier=="loadVaultPreferences");
        Services.GetRequiredService<NavigationManager>().NavigateTo(NoteAddress.Url(second));
        var sidebar=Render<VaultList>(p=>p.Add(x=>x.Project,new ProjectSummaryDto(project,"Project","DOCS",null,null,ProjectRole.ReadWrite,true)).Add(x=>x.Compact,true));
        sidebar.WaitForAssertion(()=>Assert.Contains("design-docs-nav-current",sidebar.Find(".mud-nav-item").ClassList));
        await cut.InvokeAsync(()=>cut.Instance.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string,object?>{{nameof(DesignDocsPage.RouteNoteKey),"invalid"}})));
        Assert.Contains("Note not found",cut.Markup);
        await cut.InvokeAsync(()=>cut.Instance.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string,object?>{{nameof(DesignDocsPage.RouteNoteKey),NoteAddress.Encode(first)}})));
        cut.WaitForAssertion(()=>Assert.Equal(first.ToString(),cut.Find("[data-note]").GetAttribute("data-note")));
    }
    private sealed class RecordingWorkspace : ISoloWorkspace
    {
        public int Entered,Disposed;
        public IDisposable Enter(WorkspaceOptions options){Entered++;return new Lease(this);}
        private sealed class Lease(RecordingWorkspace owner):IDisposable{public void Dispose()=>owner.Disposed++;}
    }
}
