using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.State;
using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Tests;

public sealed class AttachmentsDialogComponentTests : ComponentTestBase
{
    private sealed class Drawer : ISoloTaskDrawer
    {
        public Task OpenAsync(Guid id,CancellationToken ct=default)=>Task.CompletedTask;
        public event Action<TaskDrawerChange>? Changed{add{}remove{}}
        public event Action? Closed{add{}remove{}}
    }
    private readonly Guid _project=Guid.NewGuid(),_vault=Guid.NewGuid(),_note=Guid.NewGuid(),_other=Guid.NewGuid();
    private ImageDto _photo=null!,_lost=null!;private SketchDto _flow=null!;
    private (IRenderedComponent<MudBlazor.MudDialogProvider> Dialogs,IRenderedComponent<NoteEditor> Editor,StubHttp Http,BunitJSModuleInterop Js) Mount(bool canWrite=true)
    {
        var http=new StubHttp();var live=new FakeLiveTopics();
        _photo=new(Guid.NewGuid(),AttachmentPaths.ForNote(_note,"photo.png"),"image/png",4,3,10,2);
        _lost=new(Guid.NewGuid(),AttachmentPaths.ForNote(_note,"old.png"),"image/png",4,3,10,5);
        _flow=new(Guid.NewGuid(),_other,"Flow","A4","portrait","dots",null,null,7);
        http.RouteJson("api/users/me",new UserDto(Guid.NewGuid(),"alex@example.test","Alex",null,false));
        http.Route("colors",System.Net.HttpStatusCode.OK,()=>new ColorDto(Guid.NewGuid(),0),HttpMethod.Post);
        http.Route("colors",System.Net.HttpStatusCode.OK,()=>Array.Empty<ColorDto>(),HttpMethod.Get);
        http.Route("/images/",System.Net.HttpStatusCode.NoContent,(object?)null,HttpMethod.Delete);
        http.RouteJson("/attachments",new AttachmentListDto([_photo,_lost],[_flow],new Dictionary<Guid,string[]>{[_note]=[],[_other]=["Flow.sketch"]}));
        http.RouteJson("/images",new[]{_photo,_lost});http.RouteJson("/sketches",new[]{_flow});
        http.RouteJson($"vaults/{_vault}",new TreeDto(new(_vault,"Design",false,null,1),[],[new(_note,_vault,null,"Note","","0",[],null,null,1),new(_other,_vault,null,"Combat","","0",[],null,null,1)]));
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());Services.AddSingleton<ISoloLiveTopics>(live);
        Services.AddScoped<ISoloTaskPicker,TaskPickerService>();Services.AddSingleton<ISoloTaskDrawer>(new Drawer());Services.AddScoped<ISoloContextMenu>(sp=>sp.GetRequiredService<ContextMenuService>());
        var module=JSInterop.SetupModule(_=>true);var editor=module.SetupModule("mountEditor",_=>true);
        editor.Setup<string[]>("embedTargets").SetResult([_photo.Path+"|300"]);
        var dialogs=Render<MudBlazor.MudDialogProvider>();
        var cut=Render<NoteEditor>(p=>p.Add(x=>x.ProjectId,_project).Add(x=>x.CanWrite,canWrite).Add(x=>x.Note,new(_note,_vault,null,"Note","","0",[],null,null,1)));
        cut.WaitForState(()=>live.Held.Any(h=>h.Topic==DocTopics.Vault(_vault)));
        return (dialogs,cut,http,editor);
    }
    private static async Task Open(IRenderedComponent<NoteEditor> cut)=>await cut.Find("[aria-label=\"Attachments\"]").ClickAsync(new());
    [Fact]
    public async Task The_dialog_groups_attachments_and_marks_ones_no_note_embeds()
    {
        var (dialogs,cut,_,_)=Mount();
        var opening=Open(cut);
        dialogs.WaitForAssertion(()=>Assert.Equal(3,dialogs.FindAll(".design-docs-attachment-row").Count));
        var groups=dialogs.FindAll(".design-docs-attachment-list");
        Assert.Equal("This note",groups[0].GetAttribute("aria-label"));Assert.Equal("Other notes",groups[1].GetAttribute("aria-label"));
        Assert.Contains("photo.png",groups[0].TextContent);Assert.Contains("old.png",groups[0].TextContent);Assert.Contains("Flow.sketch",groups[1].TextContent);
        Assert.Contains("from “Combat”",groups[1].TextContent);
        var lost=dialogs.Find($"[data-attachment=\"{_lost.Id}\"]");Assert.Contains("Not embedded",lost.TextContent);
        Assert.DoesNotContain("Not embedded",dialogs.Find($"[data-attachment=\"{_photo.Id}\"]").TextContent);
        await dialogs.FindAll("button").Single(b=>b.TextContent.Trim()=="Close").ClickAsync(new());await opening;
    }
    [Fact]
    public async Task Insert_puts_the_full_path_embed_at_the_cursor()
    {
        var (dialogs,cut,_,js)=Mount();
        var inserted=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        js.SetupVoid("insert",i=>{inserted.TrySetResult((string)i.Arguments[0]!);return true;}).SetVoidResult();
        var opening=Open(cut);
        dialogs.WaitForAssertion(()=>dialogs.Find("[aria-label=\"Insert Flow.sketch\"]"));
        await dialogs.Find("[aria-label=\"Insert Flow.sketch\"]").ClickAsync(new());await opening;
        Assert.Equal($"![[{_flow.Path}]]",await inserted.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [Fact]
    public async Task Deleting_from_the_list_confirms_deletes_and_removes_this_notes_embeds()
    {
        var (dialogs,cut,http,js)=Mount();
        var removed=new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        js.SetupVoid("removeEmbeds",i=>{removed.TrySetResult(((IEnumerable<string>)i.Arguments[0]!).ToArray());return true;}).SetVoidResult();
        js.SetupVoid("forget",_=>true).SetVoidResult();
        var opening=Open(cut);
        dialogs.WaitForAssertion(()=>dialogs.Find("[aria-label=\"Delete photo.png\"]"));
        var deleting=dialogs.Find("[aria-label=\"Delete photo.png\"]").ClickAsync(new());
        dialogs.WaitForAssertion(()=>Assert.Contains("Its embeds in this note are removed.",dialogs.Markup));
        await dialogs.FindAll("button").Single(b=>b.TextContent.Contains("Delete permanently")).ClickAsync(new());await deleting;
        Assert.Equal([_photo.Path+"|300"],await removed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains($"DELETE /api/ext/design-docs/projects/{_project}/vaults/{_vault}/images/{_photo.Id}?version=2",http.Paths);
        dialogs.WaitForAssertion(()=>Assert.Empty(dialogs.FindAll($"[data-attachment=\"{_photo.Id}\"]")));
        await dialogs.FindAll("button").Single(b=>b.TextContent.Trim()=="Close").ClickAsync(new());await opening;
    }
    [Fact]
    public async Task The_viewer_links_to_the_owning_note_only_when_it_is_another_note()
    {
        var (_,cut,_,_)=Mount();
        var owner=JsonSerializer.SerializeToElement(await cut.InvokeAsync(()=>cut.Instance.AttachmentOwner(_flow.Path,true,_flow.Id.ToString())),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(NoteAddress.Url(_other),owner.GetProperty("url").GetString());Assert.Equal("Combat",owner.GetProperty("name").GetString());
        Assert.Null(await cut.InvokeAsync(()=>cut.Instance.AttachmentOwner(_photo.Path,false,_photo.Id.ToString())));
        Assert.Null(await cut.InvokeAsync(()=>cut.Instance.AttachmentOwner(_photo.Path,false,null)));
    }
    [Fact]
    public async Task Another_notes_sketch_opens_by_its_full_path()
    {
        var (_,cut,http,_)=Mount();
        http.RouteJson("/documents/",new{updates=Array.Empty<object>()});
        Assert.NotNull(await cut.Instance.SketchPreview(_flow.Path[..^7]));Assert.Null(await cut.Instance.SketchPreview("Flow"));
    }
}
