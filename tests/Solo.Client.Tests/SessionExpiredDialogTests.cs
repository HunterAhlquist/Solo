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

    [Fact]
    public async Task There_is_always_a_way_on_even_when_signing_in_keeps_failing()
    {
        var host = await ShowDialogAsync("microsoft");

        // A browser has no instance list; signing out is the way on.
        Assert.DoesNotContain("Open instances", host.Markup);
        Assert.Contains("Sign out", host.Markup);
        Assert.Empty(host.FindAll(".mud-button-close"));
    }

    [Fact]
    public async Task The_apps_can_also_open_their_instance_list()
    {
        JSInterop.Setup<bool>("solo.desktop.canOpenInstances").SetResult(true);
        var host = await ShowDialogAsync("microsoft");

        var open = host.FindAll("button").Single(b => b.TextContent.Contains("Open instances"));
        await host.InvokeAsync(() => open.Click());

        Assert.Single(JSInterop.Invocations["solo.desktop.openInstances"]);
        // Still there: the list opens over it, and choosing an instance reloads the app.
        Assert.Contains("Your session has expired", host.Markup);
    }

    [Fact]
    public async Task Sign_out_closes_with_the_result_the_layout_signs_out_on()
    {
        Services.AddSingleton(Config("microsoft"));
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();
        var host = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var dialog = await host.InvokeAsync(() => dialogs.ShowAsync<SessionExpiredDialog>("Session expired"));

        var signOut = host.FindAll("button").Single(b => b.TextContent.Contains("Sign out"));
        await host.InvokeAsync(() => signOut.Click());

        Assert.Equal(SessionExpiredDialog.SignOutResult, (await dialog.Result)!.Data);
    }
}
