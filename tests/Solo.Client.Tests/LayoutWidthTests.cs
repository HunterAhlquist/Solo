using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Api;
using Solo.Client.Auth;
using Solo.Client.Layout;
using Solo.Client.Pages;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

public class LayoutWidthTests : ComponentTestBase
{
    [Fact]
    public void Main_content_uses_MudBlazors_centered_large_container()
    {
        this.AddAuthorization();
        Services.AddSingleton(new UserSession(new StubHttp().CreateClient()));
        Services.AddSingleton(new StubHttp().CreateClient());
        Services.AddSingleton(new AppConfig());
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();

        var layout = Render<MainLayout>();

        var container = layout.Find(".mud-main-content > .mud-container");
        Assert.Contains("mud-container-maxwidth-lg", container.ClassList);
        Assert.DoesNotContain("app-content", container.ClassList);
    }

    [Fact]
    public async Task Workspace_uses_available_space_and_restores_the_default_layout_on_leave()
    {
        this.AddAuthorization();
        Services.AddSingleton(new UserSession(new StubHttp().CreateClient()));
        Services.AddSingleton(new StubHttp().CreateClient());
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();
        var layout = Render<MainLayout>();
        var workspace = Services.GetRequiredService<WorkspaceState>();
        IDisposable lease = null!;
        await layout.InvokeAsync(() => lease=workspace.Enter(new(FullWidth:true,FillHeight:true,CollapseNavigation:true)));
        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(".workspace-container")));
        Assert.Empty(layout.FindAll(".app-footer"));
        Assert.DoesNotContain("mud-container-maxwidth-lg", layout.Find(".workspace-container").ClassList);
        await layout.InvokeAsync(lease.Dispose);
        layout.WaitForAssertion(() => Assert.NotEmpty(layout.FindAll(".app-footer")));
        Assert.Contains("mud-container-maxwidth-lg", layout.Find(".mud-main-content > .mud-container").ClassList);
    }

    [Fact]
    public void Admin_settings_uses_the_shared_readable_width_without_inline_caps()
    {
        var http = new StubHttp()
            .RouteJson("api/settings", new InstanceSettingsDto(90, true, null, 1))
            .RouteJson("api/themes", new ThemeScanResultDto([], []))
            .RouteJson("api/extensions/admin", new ExtensionAdminDto([], []))
            .RouteJson("api/admin/users", new List<AdminUserDto>());
        var api = http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new UserSession(api));

        var page = Render<InstanceSettings>();

        page.WaitForAssertion(() =>
        {
            var container = page.Find(".mud-container-maxwidth-md");
            Assert.DoesNotContain("mud-container--gutters", container.ClassList);
            Assert.DoesNotContain("max-width:", page.Markup);
        });
    }
}
