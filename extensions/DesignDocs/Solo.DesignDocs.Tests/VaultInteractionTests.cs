using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.Client.State;
using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Tests;

public sealed class VaultInteractionTests : ComponentTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Returning_to_a_loaded_vault_clears_a_bad_note_link_error(bool missing)
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();var gone=Guid.NewGuid();
        var http=new StubHttp();
        http.RouteJson($"api/projects/{project}",new ProjectDto(project,"Project","DOCS",null,null,1,[]));
        http.RouteJson("api/projects",new[]{new ProjectSummaryDto(project,"Project","DOCS",null,null,ProjectRole.ReadWrite,true)});
        http.RouteJson("api/users/me",new UserDto(Guid.NewGuid(),"fixture@example.test","Fixture",null,true));
        http.RouteJson($"vaults/{vault}",new TreeDto(new(vault,"Vault",false,null,1),[],[new(note,vault,null,"Note","","0",[],null,null,1)]));
        http.RouteJson($"notes/{note}/locate",new LocateDto(project,"DOCS",vault,note));
        http.Route($"notes/{gone}/locate",HttpStatusCode.NotFound);
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());Services.AddSingleton<ISoloLiveTopics>(new FakeLiveTopics());
        Services.AddScoped<ISoloWorkspace>(sp=>sp.GetRequiredService<WorkspaceState>());
        Services.AddScoped<ISoloContextMenu>(sp=>sp.GetRequiredService<ContextMenuService>());
        ComponentFactories.AddStub<NoteEditor>();ComponentFactories.AddStub<DocHierarchy>();
        var module=JSInterop.SetupModule(_=>true);module.Setup<VaultViewPreferences>("loadVaultPreferences",_=>true).SetResult(new([],note,125));
        var cut=Render<DesignDocsPage>(p=>p.Add(x=>x.RouteNoteKey,NoteAddress.Encode(note)));
        cut.WaitForAssertion(()=>Assert.Single(cut.FindAll(".design-docs-page")));
        await cut.InvokeAsync(()=>cut.Instance.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string,object?>{{nameof(DesignDocsPage.RouteNoteKey),missing?NoteAddress.Encode(gone):"invalid"}})));
        Assert.Contains("Note not found or unavailable.",cut.Markup);
        await cut.InvokeAsync(()=>cut.Instance.SetParametersAsync(ParameterView.FromDictionary(new Dictionary<string,object?>{
            {nameof(DesignDocsPage.RouteNoteKey),null},{nameof(DesignDocsPage.ProjectAlias),"DOCS"},{nameof(DesignDocsPage.VaultId),vault}})));
        Assert.DoesNotContain("Note not found",cut.Markup);Assert.Single(cut.FindAll(".design-docs-page"));
        Assert.Single(module.Invocations,i=>i.Identifier=="loadVaultPreferences");
    }

    [Fact]
    public void Navigation_updates_vault_highlights_without_refetching_any_list()
    {
        var project=new ProjectSummaryDto(Guid.NewGuid(),"Project","DOCS",null,null,ProjectRole.ReadWrite,false);
        var first=Guid.NewGuid();var second=Guid.NewGuid();var http=new StubHttp();
        http.RouteJson("vaults",new VaultListDto(false,[new(first,"First",false,null,1),new(second,"Second",false,null,1)]));
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());Services.AddSingleton<ISoloLiveTopics>(new FakeLiveTopics());
        var navigation=Services.GetRequiredService<NavigationManager>();navigation.NavigateTo($"/docs/DOCS/{first}");
        var cut=Render<VaultList>(p=>p.Add(x=>x.Project,project).Add(x=>x.Compact,true));
        cut.WaitForAssertion(()=>Assert.Equal("First",cut.Find("[aria-current=page]").TextContent.Trim()));
        var before=http.Requests.Count;
        navigation.NavigateTo($"/docs/DOCS/{second}");
        cut.WaitForAssertion(()=>Assert.Equal("Second",cut.Find("[aria-current=page]").TextContent.Trim()));
        Assert.Equal(before,http.Requests.Count);
    }

    [Fact]
    public async Task Import_can_be_canceled_without_closing_or_losing_the_created_vault()
    {
        var vault=new VaultDto(Guid.NewGuid(),"Imported",false,null,1);var http=new StubHttp();http.RouteJson("vaults",vault);
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());
        var transfer=new BlockingImport();Services.AddSingleton<IJSRuntime>(new ImportRuntime(transfer));
        var host=Render<MudDialogProvider>();var dialogs=Services.GetRequiredService<IDialogService>();
        await host.InvokeAsync(()=>dialogs.ShowAsync<CreateVaultDialog>("New design doc",new DialogParameters<CreateVaultDialog>{{x=>x.ProjectId,Guid.NewGuid()}}));
        host.Find("input").Input("Imported");
        await host.FindAll("button").Single(b=>b.TextContent.Contains("Import an Obsidian")).ClickAsync(new MouseEventArgs());
        var importing=host.FindAll("button").Single(b=>b.TextContent.Trim()=="Create").ClickAsync(new MouseEventArgs());
        try {
            await transfer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cancel=host.FindAll("button").Single(b=>b.TextContent.Trim()=="Cancel");Assert.False(cancel.HasAttribute("disabled"));
            await cancel.ClickAsync(new MouseEventArgs());await importing;
            Assert.True(transfer.Canceled);Assert.Contains("Import canceled",host.Markup);Assert.Contains("Open design doc",host.Markup);
        } finally {transfer.Release.TrySetResult();await importing;}
    }

    private sealed class ImportRuntime(BlockingImport transfer) : IJSRuntime,IJSObjectReference
    {
        public ValueTask<T> InvokeAsync<T>(string name,object?[]? args)=>InvokeAsync<T>(name,CancellationToken.None,args);
        public ValueTask<T> InvokeAsync<T>(string name,CancellationToken ct,object?[]? args)=>ValueTask.FromResult(
            name=="createTransfer"?(T)(object)transfer:name=="import"?(T)(object)this:default!);
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }

    private sealed class BlockingImport : IJSObjectReference
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Canceled {get;private set;}
        public ValueTask<T> InvokeAsync<T>(string name,object?[]? args)=>InvokeAsync<T>(name,CancellationToken.None,args);
        public async ValueTask<T> InvokeAsync<T>(string name,CancellationToken ct,object?[]? args) {
            if(name=="inspect")return (T)(object)new ImportSummary(2,10,[],[]);
            if(name=="import") {
                Started.TrySetResult();
                try {await Release.Task.WaitAsync(ct);}catch(OperationCanceledException){Canceled=true;throw;}
            }
            return default!;
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
