using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// Long-lived API sessions (the desktop app's credential): exchange, use,
/// revocation, expiry, and the rule that a session can never mint another.
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
}
