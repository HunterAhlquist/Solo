using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.State;
using Solo.DesignDocs.Client;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Tests;

/// <summary>
/// The floating formatting panel over a note's text selection. The editor reports
/// which styles the selection has; the panel shows them and sends toggles back.
/// </summary>
public sealed class NoteFormatComponentTests : ComponentTestBase
{
    private sealed class Drawer : ISoloTaskDrawer
    {
        public Task OpenAsync(Guid id,CancellationToken ct=default)=>Task.CompletedTask;
        public event Action<TaskDrawerChange>? Changed{add{}remove{}}
        public event Action? Closed{add{}remove{}}
    }
    private (IRenderedComponent<MudBlazor.MudDialogProvider> Dialogs,IRenderedComponent<NoteEditor> Editor,BunitJSModuleInterop Js) Mount(bool canWrite=true)
    {
        var http=new StubHttp();var live=new FakeLiveTopics();var vault=Guid.NewGuid();
        http.RouteJson("api/users/me",new UserDto(Guid.NewGuid(),"alex@example.test","Alex",null,false));
        http.Route("colors",System.Net.HttpStatusCode.OK,()=>new ColorDto(Guid.NewGuid(),0),HttpMethod.Post);
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());Services.AddSingleton<ISoloLiveTopics>(live);
        Services.AddScoped<ISoloTaskPicker,TaskPickerService>();Services.AddSingleton<ISoloTaskDrawer>(new Drawer());Services.AddScoped<ISoloContextMenu>(sp=>sp.GetRequiredService<ContextMenuService>());
        var module=JSInterop.SetupModule(_=>true);var editor=module.SetupModule("mountEditor",_=>true);
        editor.SetupVoid("format",_=>true).SetVoidResult();editor.SetupVoid("focus").SetVoidResult();
        var dialogs=Render<MudBlazor.MudDialogProvider>();
        var cut=Render<NoteEditor>(p=>p.Add(x=>x.ProjectId,Guid.NewGuid()).Add(x=>x.CanWrite,canWrite).Add(x=>x.Note,new(Guid.NewGuid(),vault,null,"Note","","0",[],null,null,1)));
        cut.WaitForState(()=>live.Held.Any(h=>h.Topic==DocTopics.Vault(vault)));
        return (dialogs,cut,editor);
    }
    private static string State(bool bold=false,bool link=false)=>System.Text.Json.JsonSerializer.Serialize(new{bold,italic=false,underline=false,highlight=false,link});
    private static bool Sent(BunitJSModuleInterop js,string kind,string? url)=>
        js.Invocations.Any(i=>i.Identifier=="format"&&(string?)i.Arguments[0]==kind&&(string?)i.Arguments[1]==url);

    [Fact]
    public async Task The_panel_shows_only_while_text_is_selected_and_lights_the_styles_it_has()
    {
        var (_,cut,_)=Mount();
        Assert.Empty(cut.FindAll(".design-docs-note-format-palette"));
        await cut.InvokeAsync(()=>cut.Instance.FormatChanged(State(bold:true)));
        Assert.Equal("true",cut.Find("[aria-label='Bold']").GetAttribute("aria-pressed"));
        Assert.Equal("false",cut.Find("[aria-label='Highlight']").GetAttribute("aria-pressed"));
        await cut.InvokeAsync(()=>cut.Instance.FormatChanged("null"));
        Assert.Empty(cut.FindAll(".design-docs-note-format-palette"));
    }

    [Fact]
    public async Task Read_members_get_no_panel()
    {
        var (_,cut,_)=Mount(canWrite:false);
        await cut.InvokeAsync(()=>cut.Instance.FormatChanged(State()));
        Assert.Empty(cut.FindAll(".design-docs-note-format-palette"));
    }

    [Fact]
    public async Task A_style_button_toggles_that_style()
    {
        var (_,cut,js)=Mount();
        await cut.InvokeAsync(()=>cut.Instance.FormatChanged(State()));
        await cut.Find("[aria-label='Highlight']").ClickAsync(new());
        Assert.True(Sent(js,"highlight",null));
    }

    [Fact]
    public async Task Linking_asks_for_the_address_and_unlinking_does_not()
    {
        var (dialogs,cut,js)=Mount();
        await cut.InvokeAsync(()=>cut.Instance.FormatChanged(State()));
        var linking=cut.Find("[aria-label='Link']").ClickAsync(new());
        dialogs.WaitForAssertion(()=>dialogs.Find("input"));
        await dialogs.Find("input").InputAsync(new(){Value="https://example.test"});
        await dialogs.FindAll("button").Single(b=>b.TextContent.Trim()=="Save").ClickAsync(new());await linking;
        Assert.True(Sent(js,"link","https://example.test"));

        await cut.InvokeAsync(()=>cut.Instance.FormatChanged(State(link:true)));
        await cut.Find("[aria-label='Remove link']").ClickAsync(new());
        Assert.True(Sent(js,"link",null));
    }
}
