using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Api;
using Solo.Client.Auth;
using Solo.Client.Pages;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// A signed-in account the API allows nothing (403 on <c>/api/users/me</c>) must
/// be signed back out, not left staring at an empty app. The session raises the
/// signal; the layout drops the credential and lands on sign-in with the notice.
/// </summary>
public class UserSessionAccessDeniedTests
{
    [Fact]
    public async Task A_403_profile_raises_the_access_denied_signal_and_still_throws()
    {
        var session = new UserSession(new StubHttp().Respond(HttpStatusCode.Forbidden).CreateClient());
        var raised = false;
        session.AccessDenied += () => raised = true;

        await Assert.ThrowsAsync<ApiException>(() => session.LoadAsync());

        Assert.True(raised);
    }

    [Fact]
    public async Task Other_failures_are_not_mistaken_for_a_banned_account()
    {
        // A 500 (or a proxy hiccup) means retry later, not "you're not welcome".
        var session = new UserSession(new StubHttp().Respond(HttpStatusCode.InternalServerError).CreateClient());
        var raised = false;
        session.AccessDenied += () => raised = true;

        await Assert.ThrowsAsync<ApiException>(() => session.LoadAsync());

        Assert.False(raised);
    }

    [Fact]
    public async Task A_successful_load_stays_quiet()
    {
        var http = new StubHttp().RespondJson(new UserDto(Guid.NewGuid(), "h@x.io", "Hunter", null, false));
        var session = new UserSession(http.CreateClient());
        var raised = false;
        session.AccessDenied += () => raised = true;

        await session.LoadAsync();

        Assert.False(raised);
    }
}

/// <summary>
/// The layout owns the reaction: on the signal it drops the Google credential
/// and hard-navigates to sign-in carrying the unauthorized error.
/// </summary>
public class MainLayoutAccessDeniedTests : ComponentTestBase
{
    [Fact]
    public async Task The_denied_signal_signs_the_user_out_and_lands_on_sign_in_with_the_notice()
    {
        this.AddAuthorization();
        var session = new UserSession(new StubHttp().Respond(HttpStatusCode.Forbidden).CreateClient());
        Services.AddSingleton(session);
        Services.AddSingleton(new AppConfig
        {
            Auth = new AuthConfigDto("https://accounts.google.com", "client-id", "google", true),
        });
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();

        var layout = Render<Solo.Client.Layout.MainLayout>();

        // Whoever hydrates the session first trips the 403; the layout reacts.
        await Assert.ThrowsAsync<ApiException>(() => session.LoadAsync());

        var nav = Services.GetRequiredService<NavigationManager>();
        layout.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.google.signOut");
            Assert.EndsWith("signin?error=unauthorized", nav.Uri);
        });
    }
}

/// <summary>The sign-in page explains why the app just threw the user out.</summary>
public class SignInUnauthorizedNoticeTests : ComponentTestBase
{
    private void RegisterServices()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new AppConfig
        {
            Auth = new AuthConfigDto("https://accounts.google.com", "client-id", "google", true),
        });
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();
    }

    [Fact]
    public void Arriving_with_the_unauthorized_error_shows_the_notice()
    {
        RegisterServices();
        Services.GetRequiredService<NavigationManager>().NavigateTo("signin?error=unauthorized");

        var page = Render<SignIn>();

        Assert.Contains("isn't authorized to use this instance", page.Markup);
    }

    [Fact]
    public void A_plain_visit_shows_no_error()
    {
        RegisterServices();
        Services.GetRequiredService<NavigationManager>().NavigateTo("signin");

        var page = Render<SignIn>();

        Assert.DoesNotContain("isn't authorized", page.Markup);
    }
}
