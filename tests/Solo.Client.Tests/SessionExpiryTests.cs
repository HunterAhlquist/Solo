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

/// <summary>Builds unsigned JWTs with the shape GIS credentials have.</summary>
internal static class FakeCredential
{
    public static string ExpiringAt(DateTimeOffset expires)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = "https://accounts.google.com",
            ["sub"] = "1234567890",
            ["name"] = "Hunter Ahlquist",
            ["exp"] = expires.ToUnixTimeSeconds(),
        });

        return $"{Base64Url("{\"alg\":\"RS256\"}")}.{Base64Url(payload)}.signature-not-checked-here";
    }

    public static string Live() => ExpiringAt(DateTimeOffset.UtcNow.AddHours(1));

    public static string Expired() => ExpiringAt(DateTimeOffset.UtcNow.AddMinutes(-5));

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Google fixes its credential's life at ~1 hour, so the session outliving it
/// depends entirely on silent renewal: an expired stored token gets one FedCM
/// re-prompt before the user is treated as signed out.
/// </summary>
public class GoogleSilentRenewalTests : SoloBunitContext
{
    private static AppConfig GoogleConfig() => new()
    {
        Auth = new AuthConfigDto("https://accounts.google.com", "client-id", "google", true),
    };



    private GoogleIdentityService Service(SessionExpiry? expiry = null) =>
        new(JSInterop.JSRuntime, GoogleConfig(), expiry ?? new SessionExpiry());

    [Fact]
    public async Task A_live_stored_credential_is_returned_without_prompting()
    {
        JSInterop.Setup<string>("solo.google.token").SetResult(FakeCredential.Live());

        await using var google = Service();

        Assert.NotNull(await google.GetTokenAsync());
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.google.prompt");
    }

    [Fact]
    public async Task An_expired_credential_is_renewed_silently_and_the_fresh_one_returned()
    {
        var fresh = FakeCredential.Live();
        JSInterop.Setup<string>("solo.google.token").SetResult(FakeCredential.Expired());
        JSInterop.Setup<string>("solo.google.prompt").SetResult(fresh);

        await using var google = Service();

        Assert.Equal(fresh, await google.GetTokenAsync());
    }

    [Fact]
    public async Task A_failed_renewal_retires_the_credential_but_keeps_auto_select()
    {
        JSInterop.Setup<string>("solo.google.token").SetResult(FakeCredential.Expired());
        JSInterop.Setup<string?>("solo.google.prompt").SetResult(null);

        await using var google = Service();

        Assert.Null(await google.GetTokenAsync());
        // Retired, not signed out: the next sign-in may still happen silently.
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.google.retire");
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.google.signOut");
    }

    [Fact]
    public async Task A_renewal_that_hands_back_another_dead_token_still_signs_out()
    {
        JSInterop.Setup<string>("solo.google.token").SetResult(FakeCredential.Expired());
        JSInterop.Setup<string>("solo.google.prompt").SetResult(FakeCredential.Expired());

        await using var google = Service();

        Assert.Null(await google.GetTokenAsync());
    }

    [Fact]
    public async Task A_visitor_who_never_signed_in_is_not_prompted()
    {
        JSInterop.Setup<string?>("solo.google.token").SetResult(null);

        await using var google = Service();

        Assert.Null(await google.GetTokenAsync());
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.google.prompt");
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.google.retire");
    }
}

/// <summary>
/// The API answering 401 on a request that carried a token is the "session died
/// mid-flight" signal — it raises the re-sign-in dialog. Anonymous requests
/// bouncing off protected endpoints must not.
/// </summary>
public class IdTokenMessageHandler401Tests : SoloBunitContext
{
    private (HttpClient Client, SessionExpiry Expiry) Client(HttpStatusCode reply)
    {
        var config = new AppConfig
        {
            Auth = new AuthConfigDto("https://accounts.google.com", "client-id", "google", true),
        };
        var expiry = new SessionExpiry();
        var google = new GoogleIdentityService(JSInterop.JSRuntime, config, expiry);
        var tokens = new IdTokenProvider(
            new ServiceCollection().BuildServiceProvider(), JSInterop.JSRuntime, config, google);
        var handler = new IdTokenMessageHandler(tokens, new ConnectionIdAccessor(), expiry)
        {
            InnerHandler = new StubHttp().Respond(reply),
        };

        return (new HttpClient(handler) { BaseAddress = new Uri("https://solo.test/") }, expiry);
    }

    [Fact]
    public async Task A_401_on_an_authenticated_request_raises_the_expiry_signal()
    {
        JSInterop.Setup<string>("solo.google.token").SetResult(FakeCredential.Live());
        var (client, expiry) = Client(HttpStatusCode.Unauthorized);
        var raised = false;
        expiry.Expired += () => raised = true;

        await client.GetAsync("api/tasks");

        Assert.True(raised);
    }

    [Fact]
    public async Task A_401_on_an_anonymous_request_stays_quiet()
    {
        JSInterop.Setup<string?>("solo.google.token").SetResult(null);
        var (client, expiry) = Client(HttpStatusCode.Unauthorized);
        var raised = false;
        expiry.Expired += () => raised = true;

        await client.GetAsync("api/config/auth");

        Assert.False(raised);
    }

    [Fact]
    public async Task A_successful_authenticated_request_stays_quiet()
    {
        JSInterop.Setup<string>("solo.google.token").SetResult(FakeCredential.Live());
        var (client, expiry) = Client(HttpStatusCode.OK);
        var raised = false;
        expiry.Expired += () => raised = true;

        await client.GetAsync("api/tasks");

        Assert.False(raised);
    }
}

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
