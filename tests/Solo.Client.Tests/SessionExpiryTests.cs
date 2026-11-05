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
/// A Google credential lasts about an hour and can't be refreshed, so the web app
/// trades it for a session cookie at sign-in and never stores it. Whether the
/// page is signed in then rests on the profile delivered with the session.
/// </summary>
public class GoogleWebSessionTests : SoloBunitContext
{
    private static AppConfig GoogleConfig() => new()
    {
        Auth = new AuthConfigDto("https://accounts.google.com", "client-id", "google", true),
    };

    private const string Profile = """{"id":"6c1c3d1e-5a43-4f3e-9b1e-0e0f3a2c9d11","email":"hunter@example.com","displayName":"Hunter Ahlquist","avatarUrl":null,"isSuperAdmin":false}""";

    private GoogleIdentityService Service() => new(JSInterop.JSRuntime, GoogleConfig());

    [Fact]
    public async Task A_credential_that_buys_a_session_is_not_kept()
    {
        JSInterop.Setup<string?>("solo.session.begin", _ => true).SetResult(Profile);
        await using var google = Service();

        await google.OnCredentialReceived(FakeCredential.Live());

        // The cookie travels by itself: no bearer credential on API calls.
        Assert.Null(await google.GetTokenAsync());
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.google.token");
    }

    [Fact]
    public async Task A_stored_profile_signs_in_without_asking_google_or_the_server()
    {
        JSInterop.Setup<string?>("solo.session.user").SetResult(Profile);
        await using var google = Service();

        var (signedIn, display) = await google.GetAuthStateAsync();

        Assert.True(signedIn);
        Assert.Equal("Hunter Ahlquist", display!.Identity!.Name);
        Assert.True(await google.HoldsSessionCookieAsync());
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier is "solo.google.init" or "solo.session.probe");
    }

    [Fact]
    public async Task A_credential_that_cannot_buy_a_session_is_held_in_memory_only()
    {
        JSInterop.Setup<string?>("solo.session.begin", _ => true).SetResult(null);
        await using var google = Service();
        var credential = FakeCredential.Live();

        await google.OnCredentialReceived(credential);

        // Sent as the bearer, so the API can answer with the access-denied
        // notice an identity nobody invited should see.
        Assert.Equal(credential, await google.GetTokenAsync());
    }

    [Fact]
    public async Task A_live_credential_left_by_an_older_version_is_traded_for_a_session()
    {
        var stored = FakeCredential.Live();
        JSInterop.Setup<string?>("solo.google.token").SetResult(stored);
        JSInterop.Setup<string?>("solo.session.begin", i => (string?)i.Arguments[0] == stored).SetResult(Profile);
        await using var google = Service();

        Assert.True((await google.GetAuthStateAsync()).SignedIn);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.google.retire");
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "solo.session.probe");
    }

    [Fact]
    public async Task An_expired_credential_left_by_an_older_version_is_dropped_without_prompting()
    {
        JSInterop.Setup<string?>("solo.google.token").SetResult(FakeCredential.Expired());
        JSInterop.Setup<string?>("solo.session.probe").SetResult(null);
        await using var google = Service();

        Assert.False((await google.GetAuthStateAsync()).SignedIn);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.google.retire");
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier is "solo.session.begin" or "solo.google.init");
    }

    [Fact]
    public async Task A_session_cookie_whose_profile_was_cleared_is_found_by_asking_the_server()
    {
        JSInterop.Setup<string?>("solo.google.token").SetResult(null);
        JSInterop.Setup<string?>("solo.session.probe").SetResult(Profile);
        await using var google = Service();

        var (signedIn, display) = await google.GetAuthStateAsync();

        Assert.True(signedIn);
        Assert.Equal("Hunter Ahlquist", display!.Identity!.Name);
    }

    [Fact]
    public async Task Signing_out_revokes_the_session_and_disables_auto_select()
    {
        await using var google = Service();

        await google.SignOutAsync();

        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.session.revoke");
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "solo.google.signOut");
    }
}

/// <summary>
/// The API answering 401 on a request that carried a credential (here, the web
/// app's session cookie) is the "session died mid-flight" signal — it raises the re-sign-in dialog. Anonymous requests
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
        var google = new GoogleIdentityService(JSInterop.JSRuntime, config);
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
        JSInterop.Setup<string?>("solo.session.user").SetResult("{}");
        var (client, expiry) = Client(HttpStatusCode.Unauthorized);
        var raised = false;
        expiry.Expired += () => raised = true;

        await client.GetAsync("api/tasks");

        Assert.True(raised);
    }

    [Fact]
    public async Task A_401_on_an_anonymous_request_stays_quiet()
    {
        JSInterop.Setup<string?>("solo.session.user").SetResult(null);
        var (client, expiry) = Client(HttpStatusCode.Unauthorized);
        var raised = false;
        expiry.Expired += () => raised = true;

        await client.GetAsync("api/config/auth");

        Assert.False(raised);
    }

    [Fact]
    public async Task A_successful_authenticated_request_stays_quiet()
    {
        JSInterop.Setup<string?>("solo.session.user").SetResult("{}");
        var (client, expiry) = Client(HttpStatusCode.OK);
        var raised = false;
        expiry.Expired += () => raised = true;

        await client.GetAsync("api/tasks");

        Assert.False(raised);
    }
}
