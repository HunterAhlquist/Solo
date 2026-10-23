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
}
