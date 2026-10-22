using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solo.Client.Api;
using Solo.Client.Auth;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// An app that started on an expired session never loaded its profile: the
/// first <c>/api/users/me</c> was refused. Signing back in from the expiry
/// dialog has to fetch it, or the app bar keeps the generic icon until a reload.
/// </summary>
public class MainLayoutSessionExpiryTests : ComponentTestBase
{
    [Fact]
    public async Task Signing_back_in_from_the_expiry_dialog_loads_the_missing_profile()
    {
        this.AddAuthorization();
        var me = new UserDto(Guid.NewGuid(), "h@x.io", "Hunter", "https://example.test/p.png", false);
        var session = new UserSession(new StubHttp().RespondJson(me).CreateClient());
        Services.AddSingleton(session);
        Services.AddSingleton(new StubHttp().CreateClient());
        Services.AddSingleton(new AppConfig
        {
            Auth = new AuthConfigDto("https://accounts.google.com", "client-id", "google", true),
        });
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();

        var layout = Render<Solo.Client.Layout.MainLayout>();
        Assert.Null(session.Current);

        await layout.InvokeAsync(() => Services.GetRequiredService<SessionExpiry>().NotifyExpired());
        layout.WaitForAssertion(() => Assert.Contains("Your session has expired", layout.Markup));

        // The GIS callback delivers a fresh credential, as a real re-sign-in would.
        var google = Services.GetRequiredService<GoogleIdentityService>();
        await layout.InvokeAsync(() => google.OnCredentialReceived(FakeCredential.Live()));

        layout.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Your session has expired", layout.Markup);
            Assert.Equal(me.Id, session.Current?.Id);
        });
    }
}
