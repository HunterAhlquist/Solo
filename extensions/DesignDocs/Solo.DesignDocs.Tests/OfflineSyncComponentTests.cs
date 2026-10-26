using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.DesignDocs.Client;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.DesignDocs.Tests;

public sealed class OfflineSyncComponentTests : ComponentTestBase
{
    private readonly BunitJSModuleInterop _module;
    public OfflineSyncComponentTests()
    {
        this.AddAuthorization().SetAuthorized("Alex");
        var http=new StubHttp();http.RouteJson("api/users/me",new UserDto(Guid.NewGuid(),"alex@example.test","Alex",null,false));
        Services.AddSingleton<ISoloClientApi>(http.CreateClient());_module=JSInterop.SetupModule(_=>true);
    }
    [Fact]
    public async Task Removing_the_last_download_shows_a_plain_cloud_and_an_empty_list_message()
    {
        var popovers=Render<MudPopoverProvider>();var cut=Render<OfflineSync>();
        await cut.InvokeAsync(()=>cut.Instance.Changed(new(){Vaults=[new(){Id=Guid.NewGuid().ToString(),Name="Vault",Ready=true}]}));
        Assert.Equal(Icons.Material.Filled.Cloud,cut.FindComponent<MudMenu>().Instance.Icon);
        await cut.InvokeAsync(()=>cut.Instance.Changed(new()));
        Assert.Equal(Icons.Material.Filled.Cloud,cut.FindComponent<MudMenu>().Instance.Icon);
        cut.Find("button").Click();popovers.WaitForAssertion(()=>Assert.Contains("No downloaded vaults.",popovers.Markup));
        Assert.DoesNotContain("Downloaded vaults are synced",popovers.Markup);
    }
    [Fact]
    public async Task A_javascript_failure_shows_a_short_warning_without_its_stack_trace()
    {
        _module.Setup<OfflineSync.RemovalResult>("offlineAction",i=>Equals(i.Arguments[0],"forget"))
            .SetException(new JSException("Removal failed\n@https://test.invalid/design-docs.js:123\nwasm-function[298]"));
        var popovers=Render<MudPopoverProvider>();var dialogs=Render<MudDialogProvider>();var cut=Render<OfflineSync>();
        await cut.InvokeAsync(()=>cut.Instance.Changed(new(){Vaults=[new(){Id=Guid.NewGuid().ToString(),Name="Vault",Ready=true}]}));
        cut.Find("button").Click();
        var removing=popovers.FindAll(".mud-menu-item").Single(e=>e.TextContent.Contains("Remove download")).ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        dialogs.WaitForAssertion(()=>Assert.Contains("Open editors will switch to online saving",dialogs.Markup));
        await dialogs.FindAll("button").Single(b=>b.TextContent.Trim()=="Remove").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await removing;
        var snackbar=Assert.Single(Services.GetRequiredService<ISnackbar>().ShownSnackbars);
        Assert.Equal("Could not remove this download. Try again.",snackbar.Message?.ToString());
    }
    [Fact]
    public async Task Removing_the_last_download_also_refreshes_the_open_vaults_download_button()
    {
        var project=Guid.NewGuid();var vault=Guid.NewGuid();var status=new OfflineVaultButton.DownloadStatus{Ready=true};
        _module.Setup<OfflineVaultButton.DownloadStatus>("offlineStatus",_=>true).SetResult(status);
        var sync=Render<OfflineSync>();var button=Render<OfflineVaultButton>(p=>p.Add(x=>x.ProjectId,project).Add(x=>x.VaultId,vault));
        await sync.InvokeAsync(()=>sync.Instance.Changed(new(){Vaults=[new(){Id=vault.ToString(),ProjectId=project,Ready=true}]}));
        button.WaitForAssertion(()=>Assert.Equal(Icons.Material.Filled.OfflinePin,button.FindComponent<MudIconButton>().Instance.Icon));
        status.Ready=false;await sync.InvokeAsync(()=>sync.Instance.Changed(new()));
        button.WaitForAssertion(()=>Assert.Equal(Icons.Material.Filled.CloudDownload,button.FindComponent<MudIconButton>().Instance.Icon));
    }

}
