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
