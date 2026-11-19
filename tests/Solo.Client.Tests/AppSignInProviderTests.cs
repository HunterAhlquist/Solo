using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Auth;
using Solo.Client.Pages;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The sign-in page in an app offers the app's own sign-in for every provider,
/// not only Google: a Microsoft or generic instance's "Continue with" would
/// navigate the app's web view to the provider, which the iPad app opens in
/// Safari and the desktop shell's CSP blocks.
/// </summary>
public class AppSignInProviderTests : ComponentTestBase
{
    private void RegisterServices(string provider, bool inApp)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<bool>("solo.desktop.available").SetResult(inApp);
        Services.AddSingleton(new AppConfig
        {
            Auth = new AuthConfigDto("https://login.example.test/tenant/v2.0", "client-id", provider, true),
            InApp = inApp,
        });
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("signin");
    }

    [Theory]
    [InlineData("microsoft")]
    [InlineData("generic")]
    public void In_an_app_another_provider_hands_sign_in_to_the_app(string provider)
    {
        RegisterServices(provider, inApp: true);

        var page = Render<SignIn>();

        page.WaitForAssertion(() => Assert.Contains("Authorize in browser", page.Markup));
        Assert.DoesNotContain("Continue with", page.Markup);
        // Google's own setting has no meaning for a session the app holds.
        Assert.DoesNotContain("Stay signed in", page.Markup);

        page.FindAll("button").Single(b => b.TextContent.Contains("Authorize in browser")).Click();
        page.WaitForAssertion(() =>
            Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.desktop.beginSignIn"));
    }

    [Theory]
    [InlineData("microsoft")]
    [InlineData("generic")]
    public void On_the_web_another_provider_keeps_its_redirect(string provider)
    {
        RegisterServices(provider, inApp: false);

        var page = Render<SignIn>();

        Assert.Contains($"Continue with {provider}", page.Markup);
        Assert.DoesNotContain("Authorize in browser", page.Markup);
    }
}
