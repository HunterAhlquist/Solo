using System.Net;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Auth;
using Solo.Client.Components;
using Solo.Client.Hosting;
using Solo.Client.Layout;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The client inside an app that shows it in a web view of its own
/// (<see cref="HostShell"/>): only the page, signed in with the session the
/// app handed over, and telling the app the three things it may.
/// </summary>
public class HostShellTests : ComponentTestBase
{
    private IRenderedComponent<MainLayout> Layout(bool hosted)
    {
        // Signed out, as the other layout tests are: the navigation and the task drawer
        // want a signed-in client's services, and neither is what these are about.
        this.AddAuthorization();
        Services.AddSingleton(new HostShell(hosted));
        Services.AddSingleton(new UserSession(new StubHttp().CreateClient()));
        Services.AddSingleton(new StubHttp().CreateClient());
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();
        return Render<MainLayout>(parameters => parameters.Add(l => l.Body, "<p id=\"page\">The page</p>"));
    }

    [Fact]
    public void Hosted_the_layout_is_the_page_and_nothing_around_it()
    {
        var layout = Layout(hosted: true);

        Assert.NotNull(layout.Find("#page"));
        Assert.Empty(layout.FindAll(".mud-appbar"));
        Assert.Empty(layout.FindAll(".app-footer"));
    }

    [Fact]
    public void In_a_browser_the_layout_keeps_its_bars()
    {
        var layout = Layout(hosted: false);

        Assert.NotEmpty(layout.FindAll(".mud-appbar"));
        Assert.NotEmpty(layout.FindAll(".app-footer"));
        Assert.Empty(JSInterop.Invocations["solo.host.post"]);
    }

    [Fact]
    public void Hosted_the_app_is_told_when_the_client_is_up()
    {
        Layout(hosted: true);

        var said = Assert.Single(JSInterop.Invocations["solo.host.post"]);
        Assert.Equal("ready", said.Arguments[0]);
    }

    [Fact]
    public void A_link_to_a_screen_the_app_has_goes_to_the_app_and_the_page_stays()
    {
        JSInterop.Setup<bool>("solo.host.claim", "projects/DMD").SetResult(true);
        JSInterop.Setup<bool>("solo.host.claim", "roadmap/DMD").SetResult(false);
        Layout(hosted: true);
        var navigation = Services.GetRequiredService<BunitNavigationManager>();

        navigation.NavigateTo("projects/DMD");
        navigation.NavigateTo("roadmap/DMD");

        // Newest first: the extension's own page was followed, the app's was not.
        Assert.Equal(NavigationState.Succeeded, navigation.History.First().State);
        Assert.EndsWith("roadmap/DMD", navigation.History.First().Uri);
        Assert.Equal(NavigationState.Prevented, navigation.History.Last().State);
        Assert.EndsWith("roadmap/DMD", navigation.Uri);
    }

    [Fact]
    public async Task Hosted_a_refused_session_is_the_apps_to_renew()
    {
        var layout = Layout(hosted: true);

        await layout.InvokeAsync(() => Services.GetRequiredService<SessionExpiry>().NotifyExpired());

        layout.WaitForAssertion(() =>
            Assert.Contains(JSInterop.Invocations["solo.host.post"], said => Equals(said.Arguments[0], "sessionRefused")));
        Assert.Empty(layout.FindAll(".mud-dialog"));
    }

    [Fact]
    public void Hosted_an_unreachable_page_tells_the_app_instead_of_going_to_sign_in()
    {
        Services.AddSingleton(new HostShell(true));
        var navigation = Services.GetRequiredService<BunitNavigationManager>();
        var before = navigation.Uri;

        var redirect = Render<RedirectToSignIn>();

        Assert.Equal(before, navigation.Uri);
        Assert.Contains("Sign in again in the app", redirect.Markup);
        Assert.Equal("sessionRefused", Assert.Single(JSInterop.Invocations["solo.host.post"]).Arguments[0]);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("oidc")]
    public async Task Hosted_the_credential_is_the_handed_over_session_whatever_the_provider(string provider)
    {
        JSInterop.Setup<string?>("solo.session.token").SetResult("solo_handed");
        JSInterop.Setup<string?>("solo.session.user").SetResult("{}");
        var config = new AppConfig { Auth = new AuthConfigDto("https://idp.test", "client-id", provider, true) };
        var tokens = new IdTokenProvider(
            new ServiceCollection().BuildServiceProvider(), JSInterop.JSRuntime, config,
            new GoogleIdentityService(JSInterop.JSRuntime, config), new HostShell(true));

        Assert.Equal("solo_handed", await tokens.GetTokenAsync());
        // The session travels as a bearer credential here, never as the web's cookie.
        Assert.False(await tokens.HoldsSessionCookieAsync());
    }

    [Fact]
    public async Task Hosted_signed_in_means_a_session_was_handed_over()
    {
        var provider = new HostedAuthenticationStateProvider(JSInterop.JSRuntime);
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated ?? false);

        JSInterop.Setup<string?>("solo.session.token").SetResult("solo_handed");
        JSInterop.Setup<string?>("solo.session.user").SetResult("""{"displayName":"Ada","email":"ada@example.test"}""");
        var state = await provider.GetAuthenticationStateAsync();

        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.Equal("Ada", state.User.Identity?.Name);
    }

    [Fact]
    public async Task Detection_is_false_without_the_host_object_or_the_script()
    {
        Assert.False((await HostShell.DetectAsync(JSInterop.JSRuntime)).IsHosted);

        JSInterop.Setup<bool>("solo.host.available").SetResult(true);
        Assert.True((await HostShell.DetectAsync(JSInterop.JSRuntime)).IsHosted);
    }
}
