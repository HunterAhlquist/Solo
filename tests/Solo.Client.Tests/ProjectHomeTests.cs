using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// Which tabs exist is a permission decision, and it is made from the signed-in
/// user. The layout hydrates that user concurrently, so the page must not read it
/// before it has arrived.
/// </summary>
public class ProjectHomeTests : BunitContext, IAsyncLifetime
{
    private readonly StubHttp _http = new();

    public ProjectHomeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);

        var api = _http.CreateClient();
        Services.AddSingleton(api);
        Services.AddSingleton(new UserSession(api));
        Services.AddSingleton(new ProjectNavStore(api));
        Services.AddScoped<RecentProjectsStore>();
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private void Route(bool isSuperAdmin, bool isOwner, ProjectRole role = ProjectRole.ReadWrite)
    {
        var member = TestData.Member(TestData.MeId, "Hunter") with { IsOwner = isOwner, Role = role };
        _http.RouteJson("/api/users/me", new UserDto(TestData.MeId, "h@x.io", "Hunter", null, isSuperAdmin));
        _http.RouteJson("/members", new[] { member });
        _http.RouteJson("/boards", Array.Empty<BoardDto>());
        _http.RouteJson("/labels", Array.Empty<LabelDto>());
        _http.RouteJson("/api/projects/by-alias/SOLO",
            new ProjectDto(TestData.ProjectId, "Solo", "SOLO", null, null, 1, [member]));
    }

    private List<string> TabsFor(bool isSuperAdmin, bool isOwner, ProjectRole role = ProjectRole.ReadWrite)
    {
        Route(isSuperAdmin, isOwner, role);
        var page = Render<Pages.ProjectHome>(p => p.Add(x => x.ProjectAlias, "SOLO"));
        return page.FindAll(".mud-tab").Select(t => t.TextContent.Trim()).ToList();
    }

    [Fact]
    public void A_super_admin_sees_the_owner_only_tabs()
    {
        // Regression: the page read Session.IsSuperAdmin before the session loaded,
        // so Audit and Settings never rendered for anyone.
        var tabs = TabsFor(isSuperAdmin: true, isOwner: false);

        Assert.Contains(tabs, t => t.Contains("Audit"));
        Assert.Contains(tabs, t => t.Contains("Settings"));
    }

    [Fact]
    public void A_project_owner_sees_the_owner_only_tabs()
    {
        var tabs = TabsFor(isSuperAdmin: false, isOwner: true);

        Assert.Contains(tabs, t => t.Contains("Audit"));
        Assert.Contains(tabs, t => t.Contains("Settings"));
    }

    [Fact]
    public void A_super_admin_with_a_read_only_role_gets_the_owner_surface_but_no_write_ui()
    {
        // The API's write policy is membership-only: super admin opens the
        // owner-surface tabs everywhere but gets no write UI (Labels) without
        // a read/write role or the owner flag.
        var tabs = TabsFor(isSuperAdmin: true, isOwner: false, role: ProjectRole.Read);

        Assert.Contains(tabs, t => t.Contains("Audit"));
        Assert.Contains(tabs, t => t.Contains("Settings"));
        Assert.DoesNotContain(tabs, t => t.Contains("Labels"));
    }

    [Fact]
    public void A_plain_member_sees_neither()
    {
        var tabs = TabsFor(isSuperAdmin: false, isOwner: false);

        Assert.DoesNotContain(tabs, t => t.Contains("Audit"));
        Assert.DoesNotContain(tabs, t => t.Contains("Settings"));
        Assert.Contains(tabs, t => t.Contains("Boards"));
    }
}
