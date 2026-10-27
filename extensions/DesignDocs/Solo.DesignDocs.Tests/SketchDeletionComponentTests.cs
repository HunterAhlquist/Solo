using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Tests;

public sealed class SketchDeletionComponentTests : ComponentTestBase
{
    private (IRenderedComponent<MudBlazor.MudDialogProvider> Dialogs,FakeLiveTopics Live,BunitJSModuleInterop Sketch,Guid Project,Guid Id) Open(bool unsaved)
    {
        var project=Guid.NewGuid();var id=Guid.NewGuid();
        var http=new StubHttp();var live=new FakeLiveTopics();
        http.RouteJson("api/users/me",new Solo.Shared.Dtos.UserDto(Guid.NewGuid(),"alex@example.test","Alex",null,false));
        http.Route("colors",System.Net.HttpStatusCode.OK,()=>new ColorDto(Guid.NewGuid(),0),HttpMethod.Post);
        http.Route("colors",System.Net.HttpStatusCode.OK,()=>Array.Empty<ColorDto>(),HttpMethod.Get);
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());Services.AddSingleton<ISoloLiveTopics>(live);
        var module=JSInterop.SetupModule(_=>true);var sketch=module.SetupModule("mountSketch",_=>true);
        sketch.Setup<bool>("unsaved").SetResult(unsaved);sketch.Setup<bool>("close",_=>true).SetResult(false);
        var dialogs=Render<MudBlazor.MudDialogProvider>();
        dialogs.InvokeAsync(()=>Services.GetRequiredService<MudBlazor.IDialogService>().ShowAsync<SketchDialog>("Map",new MudBlazor.DialogParameters<SketchDialog>{
            {x=>x.ProjectId,project},{x=>x.VaultId,Guid.NewGuid()},{x=>x.Sketch,new SketchDto(id,Guid.NewGuid(),"Map","A4","portrait","dots",null,null,1)},{x=>x.CanWrite,true}
        }));
        dialogs.WaitForState(()=>live.Held.Any(h=>h.Topic==DocTopics.Doc(id)));
        return (dialogs,live,sketch,project,id);
    }
    [Fact]
    public async Task A_deleted_sketch_says_so_in_its_title_and_goes_read_only_in_the_canvas()
    {
        var (dialogs,live,sketch,project,id)=Open(unsaved:false);
        await dialogs.InvokeAsync(()=>live.SendAsync(project,DocTopics.Doc(id),"deleted"));
        dialogs.WaitForAssertion(()=>Assert.Contains("Deleted · read-only",dialogs.Find(".design-docs-sketch-title").TextContent));
        Assert.Contains(sketch.Invocations,i=>i.Identifier=="deleted");
    }
    [Fact]
    public async Task A_deleted_sketch_with_unsaved_strokes_closes_only_after_they_are_discarded()
    {
        var (dialogs,live,sketch,project,id)=Open(unsaved:true);
        await dialogs.InvokeAsync(()=>live.SendAsync(project,DocTopics.Doc(id),"deleted"));
        dialogs.WaitForAssertion(()=>Assert.Contains("Deleted",dialogs.Find(".design-docs-sketch-title").TextContent));
        // Closing waits on the confirmation below, so it is awaited only after answering it.
        var closing=dialogs.Find(".design-docs-sketch-title .mud-button-close").ClickAsync(new());
        dialogs.WaitForAssertion(()=>Assert.Contains("can’t be saved",dialogs.Markup));
        Assert.DoesNotContain(sketch.Invocations,i=>i.Identifier=="close");
        await dialogs.FindAll("button").Single(b=>b.TextContent.Contains("Discard and close")).ClickAsync(new());await closing.WaitAsync(TimeSpan.FromSeconds(5));
        dialogs.WaitForAssertion(()=>Assert.Empty(dialogs.FindAll(".design-docs-sketch-title")));
        Assert.Contains(sketch.Invocations,i=>i.Identifier=="discard");
    }
    [Fact]
    public async Task Keeping_the_strokes_leaves_a_deleted_sketch_open()
    {
        var (dialogs,live,sketch,project,id)=Open(unsaved:true);
        await dialogs.InvokeAsync(()=>live.SendAsync(project,DocTopics.Doc(id),"deleted"));
        // Closing waits on the confirmation below, so it is awaited only after answering it.
        var closing=dialogs.Find(".design-docs-sketch-title .mud-button-close").ClickAsync(new());
        dialogs.WaitForAssertion(()=>Assert.Contains("can’t be saved",dialogs.Markup));
        await dialogs.FindAll("button").Single(b=>b.TextContent.Trim()=="Cancel").ClickAsync(new());await closing.WaitAsync(TimeSpan.FromSeconds(5));
        dialogs.WaitForAssertion(()=>Assert.DoesNotContain("can’t be saved",dialogs.Markup));
        Assert.Single(dialogs.FindAll(".design-docs-sketch-title"));Assert.DoesNotContain(sketch.Invocations,i=>i.Identifier=="discard");
    }
}
