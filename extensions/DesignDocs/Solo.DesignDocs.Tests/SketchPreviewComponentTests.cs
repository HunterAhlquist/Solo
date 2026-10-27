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

public sealed class SketchPreviewComponentTests : ComponentTestBase
{
    private sealed class Drawer : ISoloTaskDrawer
    {
        public Task OpenAsync(Guid id,CancellationToken ct=default)=>Task.CompletedTask;
        public event Action<TaskDrawerChange>? Changed{add{}remove{}}
        public event Action? Closed{add{}remove{}}
    }
    private (IRenderedComponent<NoteEditor> Cut,StubHttp Http,FakeLiveTopics Live,BunitJSModuleInterop Editor) Mount(Guid project,Guid vault,Guid note,bool canWrite=false)
    {
        var http=new StubHttp();var live=new FakeLiveTopics();
        http.RouteJson("api/users/me",new UserDto(Guid.NewGuid(),"alex@example.test","Alex",null,false));
        http.Route("colors",System.Net.HttpStatusCode.OK,()=>new ColorDto(Guid.NewGuid(),0),HttpMethod.Post);
        http.Route("colors",System.Net.HttpStatusCode.OK,()=>Array.Empty<ColorDto>(),HttpMethod.Get);
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());Services.AddSingleton<ISoloLiveTopics>(live);
        Services.AddScoped<ISoloTaskPicker,TaskPickerService>();Services.AddSingleton<ISoloTaskDrawer>(new Drawer());Services.AddScoped<ISoloContextMenu>(sp=>sp.GetRequiredService<ContextMenuService>());
        var module=JSInterop.SetupModule(_=>true);var editor=module.SetupModule("mountEditor",_=>true);
        var cut=Render<NoteEditor>(p=>p.Add(x=>x.ProjectId,project).Add(x=>x.CanWrite,canWrite).Add(x=>x.Note,new(note,vault,null,"Note","","0",[],null,null,1)));
        cut.WaitForState(()=>live.Held.Any(h=>h.Topic==DocTopics.Vault(vault)));
        return (cut,http,live,editor);
    }
    [Fact]
    public async Task Remote_sketch_changes_refresh_both_attachment_paths_only_in_the_owning_note()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();var other=Guid.NewGuid();
        var (cut,_,live,editor)=Mount(project,vault,note);
        var refreshed=new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.SetupVoid("refreshSketches",i=>{refreshed.TrySetResult((string[])i.Arguments[0]!);return true;}).SetVoidResult();
        var path=AttachmentPaths.ForNote(note,"Map.sketch");
        await cut.InvokeAsync(()=>live.SendAsync(project,DocTopics.Vault(vault),"sketch",JsonSerializer.Serialize(new{noteId=other,name="Map",path=AttachmentPaths.ForNote(other,"Map.sketch")})));
        Assert.DoesNotContain(editor.Invocations,i=>i.Identifier=="refreshSketches");
        await cut.InvokeAsync(()=>live.SendAsync(project,DocTopics.Vault(vault),"sketch",JsonSerializer.Serialize(new{noteId=note,name="Map",path})));
        var names=await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.Contains("Map",names);Assert.Contains(path[..^7],names);
    }
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public async Task Preview_preserves_local_updates_and_fetches_remote_changes_only_for_uploaded_sketches(uint version)
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();var sketch=Guid.NewGuid();
        var (cut,http,_,_)=Mount(project,vault,note);var reads=0;
        http.RouteJson("/sketches",new[]{new SketchDto(sketch,note,"Map","A4","portrait","dots",null,null,version)});
        http.Route("/documents/",System.Net.HttpStatusCode.OK,()=>new{updates=new[]{new{data=++reads==1?"local-pending":"remote-new"}}});
        var result=JsonSerializer.SerializeToElement(await cut.Instance.SketchPreview("Map"));
        var updates=result.GetProperty("updates").EnumerateArray().Select(u=>u.GetString()).ToArray();
        Assert.Contains("local-pending",updates);Assert.Equal(version==0?1:2,reads);
        if(version!=0)Assert.Contains("remote-new",updates);
    }
    [Fact]
    public async Task Acknowledged_sketches_still_resolve_from_their_temporary_paths()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();var id=Guid.NewGuid();
        var (cut,http,_,_)=Mount(project,vault,note);
        http.RouteJson("/sketches",new[]{new SketchDto(id,note,"Map (conflict 2)","A4","portrait","dots",null,null,1)});
        http.RouteJson("/documents/",new{updates=Array.Empty<object>()});
        Assert.NotNull(await cut.Instance.SketchPreview(AttachmentPaths.ForNote(note,id+"/Map")));
        Assert.Null(await cut.Instance.SketchPreview(AttachmentPaths.ForNote(Guid.NewGuid(),id+"/Map")));
    }
    [Fact]
    public async Task Acknowledged_images_still_resolve_from_their_temporary_paths()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();var id=Guid.NewGuid();
        var (cut,http,_,_)=Mount(project,vault,note);
        http.RouteBytes("/images/"+id,[1,2,3]);
        http.RouteJson("/images",new[]{new ImageDto(id,AttachmentPaths.ForNote(note,"renamed.png"),"image/png",1,1,3,1)});
        var result=JsonSerializer.SerializeToElement(await cut.Instance.Image(AttachmentPaths.ForNote(note,id+"/original.png")));
        Assert.Equal("AQID",result.GetProperty("base64").GetString());
    }
    [Fact]
    public async Task Deleting_an_attachment_sends_its_current_version_and_leaves_other_notes_sketches_alone()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();var sketch=Guid.NewGuid();var foreign=Guid.NewGuid();var image=Guid.NewGuid();
        var (cut,http,_,_)=Mount(project,vault,note,canWrite:true);
        http.Route("/sketches/",System.Net.HttpStatusCode.NoContent,(object?)null,HttpMethod.Delete);http.Route("/images/",System.Net.HttpStatusCode.NoContent,(object?)null,HttpMethod.Delete);
        http.RouteJson("/sketches",new[]{new SketchDto(sketch,note,"Map","A4","portrait","dots",null,null,7),new SketchDto(foreign,Guid.NewGuid(),"Other","A4","portrait","dots",null,null,3)});
        http.RouteJson("/images",new[]{new ImageDto(image,AttachmentPaths.ForNote(note,"photo.png"),"image/png",1,1,3,9)});
        Assert.True(await cut.InvokeAsync(()=>cut.Instance.DeleteAttachment(AttachmentPaths.ForNote(note,"Map.sketch"),true,sketch.ToString())));
        Assert.True(await cut.InvokeAsync(()=>cut.Instance.DeleteAttachment(AttachmentPaths.ForNote(note,"photo.png"),false,image.ToString())));
        Assert.False(await cut.InvokeAsync(()=>cut.Instance.DeleteAttachment("Other.sketch",true,foreign.ToString())));
        var deletes=http.Paths.Where(p=>p.StartsWith("DELETE",StringComparison.Ordinal)).ToList();
        Assert.Equal([$"DELETE /api/ext/design-docs/projects/{project}/vaults/{vault}/sketches/{sketch}?version=7",$"DELETE /api/ext/design-docs/projects/{project}/vaults/{vault}/images/{image}?version=9"],deletes);
    }
    [Fact]
    public async Task A_read_only_editor_never_deletes_an_attachment()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();
        var (cut,http,_,_)=Mount(project,vault,note);
        http.RouteJson("/sketches",new[]{new SketchDto(Guid.NewGuid(),note,"Map","A4","portrait","dots",null,null,7)});
        Assert.Null(await cut.InvokeAsync(()=>cut.Instance.ConfirmRemoveAttachment("Map.sketch",true)));
        Assert.False(await cut.InvokeAsync(()=>cut.Instance.DeleteAttachment("Map.sketch",true,Guid.NewGuid().ToString())));
        Assert.DoesNotContain(http.Paths,p=>p.StartsWith("DELETE",StringComparison.Ordinal));
    }
    [Fact]
    public async Task A_failed_delete_reports_why_and_keeps_the_embed()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();var sketch=Guid.NewGuid();
        var (cut,http,_,_)=Mount(project,vault,note,canWrite:true);
        http.Route("/sketches/",System.Net.HttpStatusCode.Conflict,new{title="Content changed",detail="Refresh and try again."},HttpMethod.Delete);
        http.RouteJson("/sketches",new[]{new SketchDto(sketch,note,"Map","A4","portrait","dots",null,null,7)});
        Assert.False(await cut.InvokeAsync(()=>cut.Instance.DeleteAttachment("Map.sketch",true,sketch.ToString())));
        Assert.Contains(http.Paths,p=>p.StartsWith("DELETE",StringComparison.Ordinal));
        cut.WaitForAssertion(()=>Assert.DoesNotContain("Connecting",cut.Find(".design-docs-note-status").TextContent));
    }
    [Fact]
    public async Task A_delete_never_falls_back_to_another_notes_image_with_the_same_name()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();var shown=Guid.NewGuid();var other=Guid.NewGuid();
        var (cut,http,_,_)=Mount(project,vault,note,canWrite:true);
        http.Route("/images/",System.Net.HttpStatusCode.NoContent,(object?)null,HttpMethod.Delete);
        // The image that was shown has been deleted by someone else; note B still has its own photo.png.
        http.RouteJson("/images",new[]{new ImageDto(other,AttachmentPaths.ForNote(Guid.NewGuid(),"photo.png"),"image/png",1,1,3,4)});
        Assert.False(await cut.InvokeAsync(()=>cut.Instance.DeleteAttachment(AttachmentPaths.ForNote(note,"photo.png"),false,shown.ToString())));
        Assert.False(await cut.InvokeAsync(()=>cut.Instance.DeleteAttachment(AttachmentPaths.ForNote(note,"photo.png"),false,null)));
        Assert.DoesNotContain(http.Paths,p=>p.StartsWith("DELETE",StringComparison.Ordinal));
    }
    [Fact]
    public async Task A_loaded_image_reports_the_id_a_delete_will_use()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();var id=Guid.NewGuid();
        var (cut,http,_,_)=Mount(project,vault,note);
        http.RouteBytes("/images/"+id,[1,2,3]);http.RouteJson("/images",new[]{new ImageDto(id,AttachmentPaths.ForNote(note,"photo.png"),"image/png",1,1,3,1)});
        var result=JsonSerializer.SerializeToElement(await cut.Instance.Image(AttachmentPaths.ForNote(note,"photo.png")),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(id,result.GetProperty("id").GetGuid());
    }
}
