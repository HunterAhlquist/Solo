using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Controllers;
using Solo.Data;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// Long-lived API sessions (the desktop app's bearer token, the web app's
/// cookie): exchange, use, revocation, expiry, and the rule that a session can
/// never mint another.
/// </summary>
[Collection("postgres")]
public sealed class SessionsApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _connectionString = null!;
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _connectionString = await postgres.CreateDatabaseAsync("sessions");
        _factory = new SoloApiFactory(_connectionString);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private async Task<SessionDto> CreateSessionAsync() =>
        await _admin.PostJson<SessionDto>("/api/auth/sessions", new { description = "test client" });

    private HttpClient WithSessionToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [SkippableFact]
    public async Task Exchange_MintsTokenThatAuthenticates()
    {
        postgres.SkipUnlessDockerAvailable();

        var session = await CreateSessionAsync();
        Assert.StartsWith("solo_", session.Token);
        Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal("admin@example.com", session.User.Email);

        using var client = WithSessionToken(session.Token);
        var me = await client.GetJson<UserDto>("/api/users/me");
        Assert.Equal(session.User.Id, me.Id);
    }

    [SkippableFact]
    public async Task SignedInDevices_ListYourOwnSessions_AndRevokeOne()
    {
        postgres.SkipUnlessDockerAvailable();

        var mine = await CreateSessionAsync();
        var spare = await CreateSessionAsync();
        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Devices", alias = "DEVICES" });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "other@example.com", role = "Read" });
        var other = _factory.As("other", "other@example.com");
        var theirs = await other.PostJson<SessionDto>("/api/auth/sessions", new { description = "their laptop" });

        using var viaMine = WithSessionToken(mine.Token);
        var listed = await viaMine.GetJson<List<AuthSessionDto>>("/api/auth/sessions");
        Assert.Equal(2, listed.Count);
        Assert.Equal("test client", Assert.Single(listed, s => s.IsCurrent).Description);

        // Metadata only: a token is readable once, when it is minted.
        var raw = await viaMine.GetStringAsync("/api/auth/sessions");
        Assert.DoesNotContain(mine.Token, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(spare.Token, raw, StringComparison.Ordinal);

        var spareId = listed.Single(s => !s.IsCurrent).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await viaMine.DeleteAsync($"/api/auth/sessions/{spareId}")).StatusCode);
        using (var viaSpare = WithSessionToken(spare.Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await viaSpare.GetAsync("/api/users/me")).StatusCode);
        }

        // Someone else's session id is not yours to revoke, and says nothing.
        using var viaTheirs = WithSessionToken(theirs.Token);
        var theirId = Assert.Single(await viaTheirs.GetJson<List<AuthSessionDto>>("/api/auth/sessions")).Id;
        Assert.Equal(HttpStatusCode.NotFound, (await viaMine.DeleteAsync($"/api/auth/sessions/{theirId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viaTheirs.GetAsync("/api/users/me")).StatusCode);
    }

    [SkippableFact]
    public async Task SigningOutOtherDevices_KeepsTheCurrentSession()
    {
        postgres.SkipUnlessDockerAvailable();

        var mine = await CreateSessionAsync();
        var laptop = await CreateSessionAsync();
        var desktop = await CreateSessionAsync();

        using var viaMine = WithSessionToken(mine.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await viaMine.DeleteAsync("/api/auth/sessions")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await viaMine.GetAsync("/api/users/me")).StatusCode);
        foreach (var revoked in new[] { laptop, desktop })
        {
            using var client = WithSessionToken(revoked.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users/me")).StatusCode);
        }

        // From a browser signed in with the provider there is no current
        // session, so every device signs out.
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync("/api/auth/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await viaMine.GetAsync("/api/users/me")).StatusCode);
    }

    [SkippableFact]
    public async Task SessionToken_CannotMintAnotherSession()
    {
        postgres.SkipUnlessDockerAvailable();

        var session = await CreateSessionAsync();
        using var client = WithSessionToken(session.Token);

        var response = await client.PostAsJsonAsync(
            "/api/auth/sessions", new { }, TestClients.Json);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Revoke_KillsTheToken()
    {
        postgres.SkipUnlessDockerAvailable();

        var session = await CreateSessionAsync();
        using var client = WithSessionToken(session.Token);

        var revoke = await client.DeleteAsync("/api/auth/sessions/current");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var afterRevoke = await client.GetAsync("/api/users/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [SkippableFact]
    public async Task ExpiredSession_IsRefused()
    {
        postgres.SkipUnlessDockerAvailable();

        var session = await CreateSessionAsync();

        var options = new DbContextOptionsBuilder<SoloDbContext>()
            .UseNpgsql(_connectionString).Options;
        await using (var db = new SoloDbContext(options))
        {
            await db.AuthSessions.ExecuteUpdateAsync(u => u.SetProperty(
                s => s.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        using var client = WithSessionToken(session.Token);
        var response = await client.GetAsync("/api/users/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // Hosted sessions: what an app hands the web client it shows in a web
    // view of its own, in place of its own session (HostedSessions).
    // ------------------------------------------------------------------

    private static async Task<HostedSessionDto> HostedAsync(HttpClient app) =>
        await app.PostJson<HostedSessionDto>("/api/auth/sessions/hosted", new { });

    [SkippableFact]
    public async Task HostedSession_IsThePerson_ForAnHour_AndStaysOffTheirListOfDevices()
    {
        postgres.SkipUnlessDockerAvailable();

        var session = await CreateSessionAsync();
        using var app = WithSessionToken(session.Token);
        var hosted = await HostedAsync(app);

        Assert.StartsWith("solo_hosted_", hosted.Token);
        Assert.NotEqual(session.Token, hosted.Token);
        Assert.InRange(hosted.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(61));

        using var page = WithSessionToken(hosted.Token);
        Assert.Equal("admin@example.com", (await page.GetJson<UserDto>("/api/users/me")).Email);
        // It is live, as any page is.
        Assert.Equal(HttpStatusCode.OK, (await page.PostAsync("/api/auth/hub-ticket", null)).StatusCode);

        // Nothing was added to what they are signed in on: it is not a device.
        Assert.Single(await app.GetJson<List<AuthSessionDto>>("/api/auth/sessions"));
    }

    /// <summary>
    /// A page is handed one so that what it holds is worth little. None of that is true
    /// if it can turn it into something that lasts.
    /// </summary>
    [SkippableFact]
    public async Task HostedSession_BuysNothing_AndManagesNothing()
    {
        postgres.SkipUnlessDockerAvailable();

        var session = await CreateSessionAsync();
        using var app = WithSessionToken(session.Token);
        using var page = WithSessionToken((await HostedAsync(app)).Token);

        // Not another of itself, a session, or a cookie.
        Assert.Equal(HttpStatusCode.Forbidden, (await page.PostAsJsonAsync("/api/auth/sessions/hosted", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await page.PostAsJsonAsync("/api/auth/sessions", new { description = "kept" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await page.PostAsync("/api/auth/sessions/cookie", null)).StatusCode);

        // Nor a look at, or a say over, what the person is signed in on.
        Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync("/api/auth/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await page.DeleteAsync("/api/auth/sessions")).StatusCode);

        // Asked to end "its" session, it has none of its own to end: the app's goes on.
        Assert.Equal(HttpStatusCode.NoContent, (await page.DeleteAsync("/api/auth/sessions/current")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/api/users/me")).StatusCode);

        // And only an app's session buys one at all: a provider sign-in has no session for it to stand for.
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.PostAsJsonAsync("/api/auth/sessions/hosted", new { })).StatusCode);
    }

    [SkippableFact]
    public async Task HostedSession_EndsWhenItSays_HoweverMuchItIsUsed()
    {
        postgres.SkipUnlessDockerAvailable();

        // A little over a second.
        using var factory = _factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:HostedSessionMinutes", "0.02"));
        var session = await CreateSessionAsync();
        using var app = factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        var hosted = await HostedAsync(app);
        using var page = factory.CreateClient();
        page.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", hosted.Token);

        Assert.Equal(HttpStatusCode.OK, (await page.GetAsync("/api/users/me")).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(1.6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await page.GetAsync("/api/users/me")).StatusCode);

        // The app's own session is as it was, and buys the next one.
        using var next = factory.CreateClient();
        next.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await HostedAsync(app)).Token);
        Assert.Equal(HttpStatusCode.OK, (await next.GetAsync("/api/users/me")).StatusCode);
    }

    [SkippableFact]
    public async Task HostedSession_DiesWithTheSessionItStandsFor_AndDoesNotKeepItAlive()
    {
        postgres.SkipUnlessDockerAvailable();

        var session = await CreateSessionAsync();
        using var app = WithSessionToken(session.Token);
        using var page = WithSessionToken((await HostedAsync(app)).Token);
        var options = new DbContextOptionsBuilder<SoloDbContext>().UseNpgsql(_connectionString).Options;

        // Used long after the session last was, it moves neither of the session's dates:
        // a page left running doesn't hold a session open that nobody else is using.
        await using (var db = new SoloDbContext(options))
        {
            await db.AuthSessions.ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LastUsedAt, DateTimeOffset.UtcNow.AddHours(-5))
                .SetProperty(s => s.ExpiresAt, DateTimeOffset.UtcNow.AddHours(1)));
        }
        Assert.Equal(HttpStatusCode.OK, (await page.GetAsync("/api/users/me")).StatusCode);
        await using (var db = new SoloDbContext(options))
        {
            var row = await db.AuthSessions.AsNoTracking().SingleAsync();
            Assert.True(row.LastUsedAt < DateTimeOffset.UtcNow.AddHours(-4));
            Assert.True(row.ExpiresAt < DateTimeOffset.UtcNow.AddHours(2));
        }

        // The session expires: so does what stood for it.
        await using (var db = new SoloDbContext(options))
        {
            await db.AuthSessions.ExecuteUpdateAsync(u => u.SetProperty(s => s.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await page.GetAsync("/api/users/me")).StatusCode);

        // The session is revoked: the same.
        var other = await CreateSessionAsync();
        using var otherApp = WithSessionToken(other.Token);
        using var otherPage = WithSessionToken((await HostedAsync(otherApp)).Token);
        Assert.Equal(HttpStatusCode.OK, (await otherPage.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await otherApp.DeleteAsync("/api/auth/sessions/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherPage.GetAsync("/api/users/me")).StatusCode);
    }

    [SkippableFact]
    public async Task HostedSession_ThatIsNotOurs_IsRefused()
    {
        postgres.SkipUnlessDockerAvailable();

        var session = await CreateSessionAsync();
        using var app = WithSessionToken(session.Token);
        var hosted = (await HostedAsync(app)).Token;

        // Made up, cut short, or changed by a character.
        foreach (var token in new[] { "solo_hosted_", "solo_hosted_not-a-token", hosted[..^6], hosted[..^1] + (hosted[^1] == 'A' ? 'B' : 'A') })
        {
            using var page = WithSessionToken(token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await page.GetAsync("/api/users/me")).StatusCode);
        }
    }

    [SkippableFact]
    public async Task GarbageSessionToken_IsRefused()
    {
        postgres.SkipUnlessDockerAvailable();

        using var client = WithSessionToken("solo_definitely-not-a-real-token");
        var response = await client.GetAsync("/api/users/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Hub_AcceptsSessionTokenViaQueryString()
    {
        postgres.SkipUnlessDockerAvailable();

        var project = await _admin.PostJson<ProjectDto>(
            "/api/projects", new { name = "Live", alias = "LIVE" });
        var board = await _admin.PostJson<BoardDto>(
            $"/api/projects/{project.Id}/boards", new { name = "Board", alias = "BRD" });

        var session = await CreateSessionAsync();
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, BoardHubConstants.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(session.Token);
            })
            .Build();

        await connection.StartAsync();
        await connection.InvokeAsync(nameof(IBoardHubServer.JoinBoard), board.Id);
    }
    // -- The web app's cookie --------------------------------------------------

    private async Task<(HttpResponseMessage Response, string Cookie)> CreateCookieSessionAsync(bool persistent = true)
    {
        var response = await _admin.PostAsJsonAsync(
            $"/api/auth/sessions/cookie?persistent={persistent}", new { description = "Safari on iOS" }, TestClients.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.Contains("=solo_"));
        return (response, setCookie);
    }

    /// <summary>A browser as it sends the cookie: name=value only, with or without the client header.</summary>
    private HttpClient WithCookie(string setCookie, bool clientHeader = true)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        if (clientHeader)
        {
            client.DefaultRequestHeaders.Add("X-Solo-Client", "1");
        }

        return client;
    }

    [SkippableFact]
    public async Task CookieExchange_SetsAnHttpOnlyCookie_AndReturnsNoToken()
    {
        postgres.SkipUnlessDockerAvailable();

        var (response, setCookie) = await CreateCookieSessionAsync();

        Assert.StartsWith("__Host-solo-session=solo_", setCookie);
        var attributes = setCookie.ToLowerInvariant();
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.Contains("path=/", attributes);
        Assert.Contains("expires=", attributes);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("solo_", body, StringComparison.Ordinal);
        Assert.Equal("admin@example.com", (await response.Content.ReadFromJsonAsync<UserDto>(TestClients.Json))!.Email);

        using var browser = WithCookie(setCookie);
        Assert.Equal("Safari on iOS", Assert.Single(
            await browser.GetJson<List<AuthSessionDto>>("/api/auth/sessions"), s => s.IsCurrent).Description);
    }

    [SkippableFact]
    public async Task CookieExchange_WithoutStaySignedIn_SetsABrowserSessionCookie()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, setCookie) = await CreateCookieSessionAsync(persistent: false);

        Assert.StartsWith("__Host-solo-tab-session=solo_", setCookie);
        Assert.DoesNotContain("expires=", setCookie.ToLowerInvariant());
        using var browser = WithCookie(setCookie);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/users/me")).StatusCode);
    }

    [SkippableFact]
    public async Task Cookie_CountsOnlyBesideTheClientHeader()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, setCookie) = await CreateCookieSessionAsync();

        // What another page on the same site could send: the browser attaches
        // the cookie, but it can't add a custom header without CORS.
        using var forged = WithCookie(setCookie, clientHeader: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.PostAsJsonAsync(
            "/api/projects", new { name = "Forged", alias = "FORGED" }, TestClients.Json)).StatusCode);

        using var browser = WithCookie(setCookie);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/users/me")).StatusCode);
    }

    [SkippableFact]
    public async Task CookieSession_CannotMintAnotherSession()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, setCookie) = await CreateCookieSessionAsync();
        using var browser = WithCookie(setCookie);

        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsJsonAsync(
            "/api/auth/sessions/cookie?persistent=true", new { }, TestClients.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsJsonAsync(
            "/api/auth/sessions", new { }, TestClients.Json)).StatusCode);
    }

    [SkippableFact]
    public async Task SigningOut_RevokesTheCookieSession_AndClearsTheCookie()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, setCookie) = await CreateCookieSessionAsync();
        using var browser = WithCookie(setCookie);

        var signOut = await browser.DeleteAsync("/api/auth/sessions/current");
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        var cleared = signOut.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(cleared, c => c.StartsWith("__Host-solo-session=;", StringComparison.Ordinal));
        Assert.Contains(cleared, c => c.StartsWith("__Host-solo-tab-session=;", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/users/me")).StatusCode);

        // The session is already gone, and signing out still answers, so the
        // browser can drop a dead cookie.
        Assert.Equal(HttpStatusCode.NoContent, (await browser.DeleteAsync("/api/auth/sessions/current")).StatusCode);
    }

    [SkippableFact]
    public async Task CookieSession_SlidingExpiry_ReissuesThePersistentCookie()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, setCookie) = await CreateCookieSessionAsync();
        var options = new DbContextOptionsBuilder<SoloDbContext>().UseNpgsql(_connectionString).Options;
        await using (var db = new SoloDbContext(options))
        {
            await db.AuthSessions.ExecuteUpdateAsync(u => u.SetProperty(
                s => s.LastUsedAt, DateTimeOffset.UtcNow.AddHours(-2)));
        }

        using var browser = WithCookie(setCookie);
        var response = await browser.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reissued = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.Contains("=solo_"));
        Assert.Equal(setCookie.Split(';')[0], reissued.Split(';')[0]);
        Assert.Contains("expires=", reissued.ToLowerInvariant());
    }

    [SkippableFact]
    public async Task CookieSession_MintsHubTickets()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, setCookie) = await CreateCookieSessionAsync();
        using var browser = WithCookie(setCookie);

        var response = await browser.PostAsync("/api/auth/hub-ticket", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
