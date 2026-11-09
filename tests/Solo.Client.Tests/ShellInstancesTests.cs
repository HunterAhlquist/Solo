using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Auth;
using Solo.Client.Pages;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The desktop and iPad apps keep their own list of instances. The sign-in
/// page links to it there, and only there: a browser has no list to open.
/// </summary>
public class ShellInstancesTests : ComponentTestBase
{
    private void RegisterServices()
    {
        Services.AddSingleton(new AppConfig
        {
            Auth = new AuthConfigDto("https://accounts.google.com", "client-id", "google", true),
        });
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();
    }

    [Fact]
    public void A_shell_with_an_instance_list_gets_a_link_to_it()
    {
        RegisterServices();
        JSInterop.Setup<bool>("solo.desktop.canOpenInstances").SetResult(true);
        var open = JSInterop.SetupVoid("solo.desktop.openInstances");

        var page = Render<SignIn>();
        page.WaitForAssertion(() => Assert.Contains("Use another instance", page.Markup));
        page.FindAll("button").Single(b => b.TextContent.Contains("Use another instance")).Click();

        Assert.Single(open.Invocations);
    }

    [Fact]
    public void A_browser_gets_no_link()
    {
        RegisterServices();

        var page = Render<SignIn>();

        Assert.DoesNotContain("Use another instance", page.Markup);
    }
}
