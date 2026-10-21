using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Solo.Api.Auth;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The desktop shell's browser sign-in: open a PKCE-bound flow, complete it in
/// an authenticated browser, receive the one-time code on loopback, and redeem
/// it exactly once from the desktop process.
/// </summary>
[Collection("postgres")]
public sealed class DesktopAuthApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    // RFC 7636 Appendix B's verifier and S256 challenge.
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const int LoopbackPort = 49152;

    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _anon = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        var connectionString = await postgres.CreateDatabaseAsync("desktopauth");
        _factory = new SoloApiFactory(connectionString);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        _anon = _factory.CreateClient();
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private Task<DesktopAuthFlowDto> StartFlowAsync(
        string verifier = Verifier,
        int loopbackPort = LoopbackPort) =>
        _anon.PostJson<DesktopAuthFlowDto>("/api/auth/desktop/start", new
        {
            description = "test desktop",
            codeChallenge = DesktopSignInFlows.PkceChallenge(verifier),
            loopbackPort,
        });

    private Task<HttpResponseMessage> CompleteAsync(HttpClient client, string state) =>
        client.PostAsJsonAsync("/api/auth/desktop/complete", new { state }, TestClients.Json);

    private Task<HttpResponseMessage> RedeemAsync(
        string state,
        string authorizationCode,
        string verifier = Verifier) =>
        _anon.PostAsJsonAsync("/api/auth/desktop/redeem", new
        {
            state,
            authorizationCode,
            codeVerifier = verifier,
        }, TestClients.Json);

    private static async Task<(Uri Callback, string Code)> CallbackAsync(HttpResponseMessage response)
    {
        var completion = (await response.Content.ReadFromJsonAsync<DesktopAuthCompletionDto>(TestClients.Json))!;
        var callback = new Uri(completion.CallbackUri);
        var code = QueryHelpers.ParseQuery(callback.Query)["code"].ToString();
        return (callback, code);
    }

    [SkippableFact]
    public async Task FullFlow_MintsSessionExactlyOnce()
    {
        postgres.SkipUnlessDockerAvailable();

        var flow = await StartFlowAsync();
        Assert.NotEmpty(flow.State);
        Assert.True(flow.ExpiresAt > DateTimeOffset.UtcNow);

        // Nothing can be redeemed before an authenticated browser completes.
        Assert.Equal(HttpStatusCode.NotFound,
            (await RedeemAsync(flow.State, new string('a', 43))).StatusCode);

        var complete = await CompleteAsync(_admin, flow.State);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var (callback, code) = await CallbackAsync(complete);
        Assert.Equal("http", callback.Scheme);
        Assert.Equal("127.0.0.1", callback.Host);
        Assert.Equal(LoopbackPort, callback.Port);
        Assert.Equal(DesktopSignInFlows.LoopbackPath, callback.AbsolutePath);
        Assert.Equal(flow.State, QueryHelpers.ParseQuery(callback.Query)["state"].ToString());
        Assert.Equal(43, code.Length);

        var redeemed = await RedeemAsync(flow.State, code);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var session = (await redeemed.Content.ReadFromJsonAsync<SessionDto>(TestClients.Json))!;
        Assert.StartsWith("solo_", session.Token);
        Assert.Equal("admin@example.com", session.User.Email);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        var me = await client.GetJson<UserDto>("/api/users/me");
        Assert.Equal(session.User.Id, me.Id);

        Assert.Equal(HttpStatusCode.NotFound,
            (await RedeemAsync(flow.State, code)).StatusCode);
    }

    [SkippableFact]
    public async Task AppFlow_ReturnsThroughTheAppSchemeAndMintsASession()
    {
        postgres.SkipUnlessDockerAvailable();

        var flow = await _anon.PostJson<DesktopAuthFlowDto>("/api/auth/desktop/start-app", new
        {
            description = "Solo for iOS",
            codeChallenge = Challenge,
        });

        var complete = await CompleteAsync(_admin, flow.State);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var (callback, code) = await CallbackAsync(complete);
        Assert.StartsWith(DesktopSignInFlows.AppCallbackUri + "?", callback.OriginalString);
        Assert.Equal(flow.State, QueryHelpers.ParseQuery(callback.Query)["state"].ToString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await RedeemAsync(flow.State, code, verifier: new string('x', 43))).StatusCode);

        var redeemed = await RedeemAsync(flow.State, code);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var session = (await redeemed.Content.ReadFromJsonAsync<SessionDto>(TestClients.Json))!;
        Assert.StartsWith("solo_", session.Token);
    }

    [SkippableTheory]
    [InlineData("short")]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM")]
    public async Task AppFlow_ValidatesThePkceChallenge(string challenge)
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _anon.PostAsJsonAsync("/api/auth/desktop/start-app",
            new { codeChallenge = challenge }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task CompletingAFlowCannotHandTheSessionToAnAnonymousPoller()
    {
        postgres.SkipUnlessDockerAvailable();

        var started = await _anon.PostJson<JsonObject>("/api/auth/desktop/start", new
        {
            description = "remote client",
            codeChallenge = Challenge,
            loopbackPort = LoopbackPort,
        });
        var state = started["state"]!.GetValue<string>();

        // Discard the browser-only loopback response. The remote initiator has
        // its verifier, but neither the authorization code nor a polling route.
        Assert.True((await CompleteAsync(_admin, state)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await RedeemAsync(state, new string('a', 43))).StatusCode);

        var legacyDeviceCode = started["deviceCode"]?.GetValue<string>() ?? "removed";
        var stolen = await _anon.PostAsJsonAsync(
            "/api/auth/desktop/poll", new { deviceCode = legacyDeviceCode }, TestClients.Json);
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
    }

    [SkippableFact]
    public async Task WrongVerifierDoesNotRedeemOrBurnTheAuthorizationCode()
    {
        postgres.SkipUnlessDockerAvailable();

        var flow = await StartFlowAsync();
        var complete = await CompleteAsync(_admin, flow.State);
        var (_, code) = await CallbackAsync(complete);

        Assert.Equal(HttpStatusCode.NotFound,
            (await RedeemAsync(flow.State, code, new string('z', 43))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await RedeemAsync(flow.State, code)).StatusCode);
    }

    [SkippableFact]
    public async Task Complete_IsSingleUse()
    {
        postgres.SkipUnlessDockerAvailable();

        var flow = await StartFlowAsync();
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(_admin, flow.State)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CompleteAsync(_admin, flow.State)).StatusCode);
    }

    [SkippableFact]
    public async Task Complete_RequiresAuthentication()
    {
        postgres.SkipUnlessDockerAvailable();

        var flow = await StartFlowAsync();
        var response = await CompleteAsync(_anon, flow.State);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The unauthenticated attempt must not have burned the flow.
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(_admin, flow.State)).StatusCode);
    }

    [SkippableFact]
    public async Task Complete_RefusesUnknownState()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await CompleteAsync(_admin, "00000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task SessionToken_CannotCompleteAFlow()
    {
        postgres.SkipUnlessDockerAvailable();

        var session = await _admin.PostJson<SessionDto>(
            "/api/auth/sessions", new { description = "existing session" });
        using var sessionClient = _factory.CreateClient();
        sessionClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", session.Token);

        var flow = await StartFlowAsync();
        var response = await CompleteAsync(sessionClient, flow.State);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Redeem_RefusesUnknownAuthorization()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await RedeemAsync(
            "00000000000000000000000000000000",
            new string('a', 43));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task SignInPage_ServedOnlyForLiveFlows()
    {
        postgres.SkipUnlessDockerAvailable();

        var flow = await StartFlowAsync();
        var live = await _anon.GetAsync($"/desktop-auth?state={flow.State}");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("text/html", live.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Continue only if you just started sign-in", await live.Content.ReadAsStringAsync());

        var stale = await _anon.GetAsync("/desktop-auth?state=never-minted");
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);

        // A completed flow's page is spent too.
        await CompleteAsync(_admin, flow.State);
        var completed = await _anon.GetAsync($"/desktop-auth?state={flow.State}");
        Assert.Equal(HttpStatusCode.NotFound, completed.StatusCode);
    }

    /// <summary>
    /// The page is nothing but its script — an inline one is dropped by the
    /// instance CSP, which would render the heading and no sign-in button.
    /// </summary>
    [SkippableFact]
    public async Task SignInPage_LoadsItsScriptFromAFile()
    {
        postgres.SkipUnlessDockerAvailable();

        var flow = await StartFlowAsync();
        var page = await _anon.GetStringAsync($"/desktop-auth?state={flow.State}");

        foreach (var tag in page.Split("<script").Skip(1))
        {
            Assert.Contains("src=", tag[..tag.IndexOf('>')]);
        }

        var script = await _anon.GetAsync("/desktop-auth.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("javascript", script.Content.Headers.ContentType?.MediaType ?? "");
        var scriptBody = await script.Content.ReadAsStringAsync();
        Assert.Contains("location.replace(callback.href)", scriptBody);
        Assert.Contains("auto_select: false", scriptBody);
        Assert.DoesNotContain("google.accounts.id.prompt", scriptBody);
    }

    [Fact]
    public void PkceChallenge_MatchesRfc7636Vector() =>
        Assert.Equal(Challenge, DesktopSignInFlows.PkceChallenge(Verifier));

    [Fact]
    public void ExpiredFlow_IsSweptEverywhere()
    {
        var flows = new DesktopSignInFlows(TimeSpan.FromMilliseconds(-1));
        var flow = flows.Start("test", Challenge, LoopbackPort, "ip:test")!;

        Assert.False(flows.IsPending(flow.State));
        Assert.False(flows.TryComplete(flow.State, Guid.NewGuid(), out _));
        Assert.False(flows.TryRedeem(flow.State, new string('a', 43), Verifier, out _, out _));
    }

    [Fact]
    public void Start_CapsPendingFlowsPerClient()
    {
        var flows = new DesktopSignInFlows();

        for (var i = 0; i < 10; i++)
        {
            Assert.NotNull(flows.Start("test", Challenge, LoopbackPort, "ip:same"));
        }

        Assert.Null(flows.Start("test", Challenge, LoopbackPort, "ip:same"));
        Assert.NotNull(flows.Start("test", Challenge, LoopbackPort, "ip:other"));
    }

    [SkippableFact]
    public async Task Start_RejectsLegacyRequestWithoutProofBinding()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _anon.PostAsJsonAsync(
            "/api/auth/desktop/start", new { description = "legacy desktop" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Start_ValidatesDescriptionLength()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _anon.PostAsJsonAsync("/api/auth/desktop/start", new
        {
            description = new string('x', 500),
            codeChallenge = Challenge,
            loopbackPort = LoopbackPort,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableTheory]
    [InlineData("too-short", 49152)]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM", 49152)]
    [InlineData(Challenge, 80)]
    public async Task Start_ValidatesPkceChallengeAndLoopbackPort(string challenge, int port)
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _anon.PostAsJsonAsync("/api/auth/desktop/start", new
        {
            codeChallenge = challenge,
            loopbackPort = port,
        }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
