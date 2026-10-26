using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.DesignDocs.Tests;

public sealed class PresenceComponentTests : ComponentTestBase
{
    [Fact]
    public void Profile_pictures_use_assigned_color_borders_and_fall_back_to_initials()
    {
        var http=new StubHttp();Services.AddSingleton<ISoloClientApi>(http.CreateClient());
        var first=Guid.NewGuid();var second=Guid.NewGuid();
        http.RouteJson("colors",new[]{new ColorDto(first,0),new ColorDto(second,1)});
        var cut=Render<DocPresenceStrip>(p=>p.Add(x=>x.ProjectId,Guid.NewGuid()).Add(x=>x.Entries,new[]{
            new TopicPresenceEntry(first,"Alex Example",PresenceMode.Editing),new TopicPresenceEntry(second,"Sam Example",PresenceMode.Viewing)}));
        cut.WaitForAssertion(()=>Assert.Equal(2,cut.FindAll("img").Count));
        Assert.Contains(first.ToString(),cut.FindAll("img")[0].GetAttribute("src"));
        var avatars=cut.FindAll(".design-docs-presence-avatar");Assert.NotEqual(avatars[0].GetAttribute("style"),avatars[1].GetAttribute("style"));
        Assert.Contains(DocPresenceColor.Value(1),avatars[1].GetAttribute("style"));
        cut.FindAll("img")[0].TriggerEvent("onerror",new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());
        Assert.Contains("AE",cut.Markup);Assert.Single(cut.FindAll("img"));
    }
    [Fact]
    public async Task Sketch_header_updates_when_collaborators_join_and_leave()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var note=Guid.NewGuid();var sketch=Guid.NewGuid();var peer=Guid.NewGuid();
        var http=new StubHttp();var live=new FakeLiveTopics();
        http.RouteJson("api/users/me",new Solo.Shared.Dtos.UserDto(Guid.NewGuid(),"alex@example.test","Alex",null,false));
        http.Route("colors",System.Net.HttpStatusCode.OK,()=>new ColorDto(Guid.NewGuid(),0),HttpMethod.Post);
        http.Route("colors",System.Net.HttpStatusCode.OK,()=>new[]{new ColorDto(peer,1)},HttpMethod.Get);
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());Services.AddSingleton<ISoloLiveTopics>(live);
        var module=JSInterop.SetupModule(_=>true);module.SetupModule("mountSketch",_=>true);
        var dialogs=Render<MudBlazor.MudDialogProvider>();
        await dialogs.InvokeAsync(()=>Services.GetRequiredService<MudBlazor.IDialogService>().ShowAsync<SketchDialog>("Map",new MudBlazor.DialogParameters<SketchDialog>{
            {x=>x.ProjectId,project},{x=>x.VaultId,vault},{x=>x.Sketch,new SketchDto(sketch,note,"Map","A4","portrait","dots",null,null,1)}
        }));
        dialogs.WaitForState(()=>live.Held.Any(h=>h.Topic==DocTopics.Doc(sketch)));
        await dialogs.InvokeAsync(()=>live.PresentAsync(project,DocTopics.Doc(sketch),new TopicPresenceEntry(peer,"Sam Example",PresenceMode.Editing)));
        dialogs.WaitForAssertion(()=>Assert.Single(dialogs.FindAll(".design-docs-sketch-title .design-docs-presence-avatar")));
        Assert.Contains(DocPresenceColor.Value(1),dialogs.Find(".design-docs-presence-avatar").GetAttribute("style"));
        Assert.Equal("Sam Example",dialogs.Find(".design-docs-presence-avatar").GetAttribute("aria-label"));
        await dialogs.InvokeAsync(()=>live.PresentAsync(project,DocTopics.Doc(sketch)));
        dialogs.WaitForAssertion(()=>Assert.Empty(dialogs.FindAll(".design-docs-sketch-title .design-docs-presence-avatar")));
    }

}
