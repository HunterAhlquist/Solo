using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Solo.Client.Auth;
using Solo.Client.Components;
using Solo.Client.Realtime;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The re-sign-in dialog: Google re-auths in place via the GIS callback, so the
/// dialog closes itself the moment a fresh credential lands and nothing navigates.
/// </summary>
public class SessionExpiredDialogTests : ComponentTestBase
{
    private static AppConfig Config(string provider) => new()
    {
        Auth = new AuthConfigDto("https://issuer.test", "client-id", provider, true),
    };

    private async Task<IRenderedComponent<MudDialogProvider>> ShowDialogAsync(string provider)
    {
        Services.AddSingleton(Config(provider));
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();

        var host = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        await host.InvokeAsync(() => dialogs.ShowAsync<SessionExpiredDialog>("Session expired"));
        return host;
    }

    [Fact]
    public async Task Google_gets_the_in_place_sign_in_button_and_the_dialog_closes_on_a_fresh_credential()
    {
        var host = await ShowDialogAsync("google");

        Assert.Contains("Your session has expired", host.Markup);

        // The GIS callback delivers a credential, exactly as a real re-sign-in would.
        var google = Services.GetRequiredService<GoogleIdentityService>();
        await host.InvokeAsync(() => google.OnCredentialReceived(FakeCredential.Live()));

        host.WaitForAssertion(() => Assert.DoesNotContain("Your session has expired", host.Markup));
    }

    [Fact]
    public async Task Redirect_providers_get_a_sign_in_button_instead()
    {
        var host = await ShowDialogAsync("microsoft");

        Assert.Contains("Sign in with microsoft", host.Markup);
    }
}
