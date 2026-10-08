using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Solo.Client.Components;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The two places a credential can be cut off from the UI: a user's own
/// signed-in devices, and the super admin's Users section.
/// </summary>
public class AccountSecurityTests : ComponentTestBase
{
    private static readonly Guid AdminId = Guid.NewGuid();
    private static readonly Guid OtherId = Guid.NewGuid();
    private static readonly DateTimeOffset Then = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly StubHttp _http = new();

    public AccountSecurityTests()
    {
        var api = _http.CreateClient();
        var session = new UserSession(api);
        session.Replace(new UserDto(AdminId, "admin@example.com", "Admin", null, true));
        Services.AddSingleton(api);
        Services.AddSingleton(session);
    }

    private async Task<IRenderedComponent<MudDialogProvider>> ShowDevicesAsync()
    {
        var host = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        await host.InvokeAsync(() => dialogs.ShowAsync<SignedInDevicesDialog>("Signed-in devices"));
        return host;
    }

    private static AuthSessionDto Session(string description, bool current = false) =>
        new(Guid.NewGuid(), description, Then, Then, Then.AddDays(30), current);

    // -- Signed-in devices ---------------------------------------------------

    [Fact]
    public async Task Devices_mark_this_one_and_offer_to_sign_out_the_rest()
    {
        var here = Session("Solo Desktop (macOS)", current: true);
        var laptop = Session("Solo Desktop (Windows)");
        var sessions = new List<AuthSessionDto> { here, laptop };
        _http.Route("api/auth/sessions/", HttpStatusCode.NoContent, (object?)null, HttpMethod.Delete)
             .Route("api/auth/sessions", HttpStatusCode.OK, () => sessions, HttpMethod.Get);

        var host = await ShowDevicesAsync();
        host.WaitForAssertion(() => Assert.Contains("This device", host.Markup));

        // One "Sign out" per other device; the current one leaves through the
        // ordinary Sign out instead.
        var signOuts = host.FindAll("button").Where(b => b.TextContent.Trim() == "Sign out").ToList();
        Assert.Single(signOuts);

        sessions = [here];
        signOuts[0].Click();

        host.WaitForAssertion(() => Assert.DoesNotContain("Solo Desktop (Windows)", host.Markup));
        Assert.Contains($"DELETE /api/auth/sessions/{laptop.Id}", _http.Paths);
    }

    [Fact]
    public async Task Signing_out_every_other_device_is_one_request()
    {
        var sessions = new List<AuthSessionDto> { Session("This one", current: true), Session("Old laptop") };
        _http.Route("api/auth/sessions", HttpStatusCode.NoContent, (object?)null, HttpMethod.Delete)
             .Route("api/auth/sessions", HttpStatusCode.OK, () => sessions, HttpMethod.Get);

        var host = await ShowDevicesAsync();
        host.WaitForAssertion(() => Assert.Contains("Old laptop", host.Markup));

        sessions = [sessions[0]];
        host.FindAll("button").Single(b => b.TextContent.Contains("Sign out all other devices")).Click();

        host.WaitForAssertion(() => Assert.DoesNotContain("Old laptop", host.Markup));
        Assert.Contains("DELETE /api/auth/sessions", _http.Paths);
    }

    [Fact]
    public async Task With_no_desktop_sessions_the_dialog_says_where_browsers_went()
    {
        _http.RouteJson("api/auth/sessions", new List<AuthSessionDto>());

        var host = await ShowDevicesAsync();

        host.WaitForAssertion(() => Assert.Contains("No desktop apps are signed in", host.Markup));
        Assert.DoesNotContain(host.FindAll("button"), b => b.TextContent.Contains("Sign out all other devices"));
    }

    // -- Users (super admin) -------------------------------------------------

    private IRenderedComponent<AdminUsersPanel> RenderUsers(params AdminUserDto[] users)
    {
        _http.Route("api/admin/users", HttpStatusCode.OK, () => users.ToList(), HttpMethod.Get);
        var panel = Render<AdminUsersPanel>();
        panel.WaitForAssertion(() => Assert.Contains(users[0].Email, panel.Markup));
        return panel;
    }

    [Fact]
    public void The_last_super_admin_cannot_be_switched_off()
    {
        var panel = RenderUsers(
            new AdminUserDto(AdminId, "admin@example.com", "Admin", null, true, 0),
            new AdminUserDto(OtherId, "other@example.com", "Other", null, false, 0));

        var switches = panel.FindAll("input[type=checkbox]");
        Assert.True(switches[0].HasAttribute("disabled"));
        Assert.False(switches[1].HasAttribute("disabled"));
    }

    [Fact]
    public async Task Signing_a_user_out_everywhere_confirms_then_revokes_everything_they_hold()
    {
        var dialogs = Render<MudDialogProvider>();
        var panel = RenderUsers(
            new AdminUserDto(AdminId, "admin@example.com", "Admin", null, true, 0),
            new AdminUserDto(OtherId, "other@example.com", "Other", null, false, 2));
        _http.Route($"api/admin/users/{OtherId}/sign-out-everywhere", HttpStatusCode.OK,
            new SignOutEverywhereResultDto(2, [new RevokedCredentialsDto("mcp", 1)]), HttpMethod.Post);

        // Offered even with no sessions: the devices count is core's alone, and
        // the user may still hold an extension's token.
        var buttons = panel.FindAll("button").Where(b => b.TextContent.Contains("Sign out everywhere")).ToList();
        Assert.All(buttons, b => Assert.False(b.HasAttribute("disabled")));

        await panel.InvokeAsync(() => buttons[1].Click());
        dialogs.WaitForAssertion(() => Assert.Contains("such as an MCP token", dialogs.Markup));
        Assert.DoesNotContain(_http.Paths, p => p.StartsWith("POST", StringComparison.Ordinal));

        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Sign out").Click();

        panel.WaitForAssertion(() =>
            Assert.Contains($"POST /api/admin/users/{OtherId}/sign-out-everywhere", _http.Paths));
    }

    [Fact]
    public async Task Granting_super_admin_asks_first()
    {
        var dialogs = Render<MudDialogProvider>();
        var panel = RenderUsers(
            new AdminUserDto(AdminId, "admin@example.com", "Admin", null, true, 0),
            new AdminUserDto(OtherId, "other@example.com", "Other", null, false, 0));
        _http.Route($"api/admin/users/{OtherId}/super-admin", HttpStatusCode.OK,
            new AdminUserDto(OtherId, "other@example.com", "Other", null, true, 0), HttpMethod.Put);

        await panel.InvokeAsync(() => panel.FindAll("input[type=checkbox]")[1].Change(true));
        dialogs.WaitForAssertion(() => Assert.Contains("including you", dialogs.Markup));

        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Make super admin").Click();

        panel.WaitForAssertion(() =>
            Assert.Contains($"PUT /api/admin/users/{OtherId}/super-admin", _http.Paths));
        Assert.Contains("\"isSuperAdmin\":true", _http.Bodies.Last());
    }
}
