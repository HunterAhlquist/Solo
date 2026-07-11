using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Solo.Api.Auth;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The desktop shell's browser sign-in (device-code style): start a flow,
/// complete it from the "browser" with a provider credential, redeem the
/// device code for a session — plus the single-use and secrecy rules that
/// make the state safe to put in a URL.
/// </summary>
[Collection("postgres")]
public sealed class DesktopAuthApiTests(PostgresFixture postgres) : IAsyncLifetime
{
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

    private Task<DesktopAuthFlowDto> StartFlowAsync() =>
        _anon.PostJson<DesktopAuthFlowDto>("/api/auth/desktop/start", new { description = "test desktop" });

    private Task<HttpResponseMessage> PollAsync(string deviceCode) =>
        _anon.PostAsJsonAsync("/api/auth/desktop/poll", new { deviceCode }, TestClients.Json);

    private Task<HttpResponseMessage> CompleteAsync(HttpClient client, string state) =>
        client.PostAsJsonAsync("/api/auth/desktop/complete", new { state }, TestClients.Json);

    [SkippableFact]
    public async Task FullFlow_MintsSessionExactlyOnce()
    {
        postgres.SkipUnlessDockerAvailable();

        var flow = await StartFlowAsync();
        Assert.NotEmpty(flow.State);
        Assert.NotEmpty(flow.DeviceCode);
        Assert.True(flow.ExpiresAt > DateTimeOffset.UtcNow);

        // Shell polls before the browser finishes: pending.
        var pending = await PollAsync(flow.DeviceCode);
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);

        // Browser completes with the (test) provider credential.
        var complete = await CompleteAsync(_admin, flow.State);
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        // The next poll redeems the device code for a working session.
        var redeemed = await PollAsync(flow.DeviceCode);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var session = (await redeemed.Content.ReadFromJsonAsync<SessionDto>(TestClients.Json))!;
        Assert.StartsWith("solo_", session.Token);
        Assert.Equal("admin@example.com", session.User.Email);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        var me = await client.GetJson<UserDto>("/api/users/me");
        Assert.Equal(session.User.Id, me.Id);

        // Exactly once: the device code is consumed with the session.
        var again = await PollAsync(flow.DeviceCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [SkippableFact]
    public async Task Complete_IsSingleUse()
    {
        postgres.SkipUnlessDockerAvailable();

        var flow = await StartFlowAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(_admin, flow.State)).StatusCode);
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
        Assert.Equal(HttpStatusCode.NoContent, (await CompleteAsync(_admin, flow.State)).StatusCode);
    }

    [SkippableFact]
    public async Task Complete_RefusesUnknownState()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await CompleteAsync(_admin, "not-a-real-state");
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
    public async Task Poll_RefusesUnknownDeviceCode()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await PollAsync("not-a-real-device-code");
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
        Assert.Contains("Sign in", await live.Content.ReadAsStringAsync());

        var stale = await _anon.GetAsync("/desktop-auth?state=never-minted");
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);

        // A completed flow's page is spent too: the link is one-shot.
        await CompleteAsync(_admin, flow.State);
        var completed = await _anon.GetAsync($"/desktop-auth?state={flow.State}");
        Assert.Equal(HttpStatusCode.NotFound, completed.StatusCode);
    }

    [Fact]
    public void ExpiredFlow_IsSweptEverywhere()
    {
        // Unit-level: the registry is in-memory, so expiry is easiest to pin
        // down directly rather than by waiting out the TTL over HTTP.
        var flows = new DesktopSignInFlows(TimeSpan.FromMilliseconds(-1));
        var flow = flows.Start("test")!;

        Assert.False(flows.IsPending(flow.State));
        Assert.False(flows.TryComplete(flow.State, Guid.NewGuid()));
        Assert.Equal(DesktopSignInFlows.PollStatus.Unknown, flows.Poll(flow.DeviceCode, out _, out _));
    }

    [SkippableFact]
    public async Task Start_ValidatesDescriptionLength()
    {
        postgres.SkipUnlessDockerAvailable();

        var response = await _anon.PostAsJsonAsync("/api/auth/desktop/start",
            new { description = new string('x', 500) }, TestClients.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
