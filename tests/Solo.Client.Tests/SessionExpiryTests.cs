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
/// depends entirely on silent renewal. A credential that dies while the page
/// holds it gets one FedCM re-prompt before the user is treated as signed out;
/// one that was already dead when the app booted is retired straight away, so
/// the sign-in page isn't held up behind a prompt that may never answer.
/// </summary>
public class GoogleSilentRenewalTests : SoloBunitContext
{
    private static AppConfig GoogleConfig() => new()
    {
        Auth = new AuthConfigDto("https://accounts.google.com", "client-id", "google", true),
    };

    private GoogleIdentityService Service(SessionExpiry? expiry = null) =>
        new(JSInterop.JSRuntime, GoogleConfig(), expiry ?? new SessionExpiry());

    /// <summary>A service holding a credential that has since expired, as after a slept laptop.</summary>
    private async Task<GoogleIdentityService> HoldingExpiredCredential()
    {
        var google = Service();
        await google.OnCredentialReceived(FakeCredential.Expired());
        return google;
    }

    [Fact]
    public async Task A_live_stored_credential_is_returned_without_prompting()
    {
        JSInterop.Setup<string>("solo.google.token").SetResult(FakeCredential.Live());

        await using var google = Service();

        Assert.NotNull(await google.GetTokenAsync());
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.google.prompt");
    }

    [Fact]
    public async Task An_expired_credential_found_at_boot_is_retired_without_prompting()
    {
        JSInterop.Setup<string>("solo.google.token").SetResult(FakeCredential.Expired());

        await using var google = Service();

        Assert.Null(await google.GetTokenAsync());
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.google.prompt");
        // Retired, not signed out: the sign-in page may still renew silently.
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.google.retire");
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.google.signOut");
        Assert.True(google.RenewOnSignInPage);
    }

    [Fact]
    public async Task A_credential_that_expires_while_held_is_renewed_silently_and_the_fresh_one_returned()
    {
        var fresh = FakeCredential.Live();
        JSInterop.Setup<string>("solo.google.prompt").SetResult(fresh);

        await using var google = await HoldingExpiredCredential();

        Assert.Equal(fresh, await google.GetTokenAsync());
    }

    [Fact]
    public async Task A_failed_renewal_retires_the_credential_but_keeps_auto_select()
    {
        JSInterop.Setup<string?>("solo.google.prompt").SetResult(null);

        await using var google = await HoldingExpiredCredential();

        Assert.Null(await google.GetTokenAsync());
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.google.retire");
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.google.signOut");
        Assert.False(google.RenewOnSignInPage);
    }

    [Fact]
    public async Task A_renewal_that_hands_back_another_dead_token_still_signs_out()
    {
        JSInterop.Setup<string>("solo.google.prompt").SetResult(FakeCredential.Expired());

        await using var google = await HoldingExpiredCredential();

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
        Assert.False(google.RenewOnSignInPage);
    }
}

/// <summary>
/// The sign-in page is where a credential that expired before boot gets its
/// silent renewal, beside a button the user can already press.
/// </summary>
public class SignInPageRenewalTests : ComponentTestBase
{
    private GoogleIdentityService Google(bool renewOnSignInPage)
    {
        var config = new AppConfig
        {
            Auth = new AuthConfigDto("https://accounts.google.com", "client-id", "google", true),
        };
        var google = new GoogleIdentityService(JSInterop.JSRuntime, config, new SessionExpiry())
        {
            RenewOnSignInPage = renewOnSignInPage,
        };
        Services.AddSingleton(google);
        return google;
    }

    [Fact]
    public void The_button_attempts_a_silent_renewal_after_a_credential_expired_at_boot()
    {
        var google = Google(renewOnSignInPage: true);

        Render<GoogleSignInButton>();

        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.google.renderButton");
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.google.prompt");
        Assert.False(google.RenewOnSignInPage);
    }

    [Fact]
    public void The_button_does_not_prompt_a_visitor_who_signed_out()
    {
        Google(renewOnSignInPage: false);

        Render<GoogleSignInButton>();

        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.google.renderButton");
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.google.prompt");
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
