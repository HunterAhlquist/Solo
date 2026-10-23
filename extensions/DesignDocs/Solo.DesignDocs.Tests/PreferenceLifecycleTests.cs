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
}
