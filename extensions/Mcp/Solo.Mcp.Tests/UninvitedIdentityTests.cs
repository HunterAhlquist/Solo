using System.Net;
using System.Net.Http.Json;
using Npgsql;

namespace Solo.Mcp.Tests;

/// <summary>
/// A valid provider sign-in is not a Solo user. Anyone with a Google account
/// can get an ID token for the instance's client ID from its own sign-in page,
/// and this extension's endpoints used to serve such a caller: its client half,
/// and MCP tokens that never expire. Core's own endpoints are covered in
/// Solo.Api.Tests.
/// </summary>
[Collection("postgres")]
public sealed class UninvitedIdentityTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string ClientAssembly = "/api/extensions/mcp/client/Solo.Mcp.Client.dll";

    private string _connectionString = null!;
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _connectionString = await postgres.CreateDatabaseAsync("uninvited");
        _factory = new McpApiFactory(_connectionString);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task An_identity_nobody_invited_gets_no_client_half_and_no_token()
    {
        postgres.SkipUnlessDockerAvailable();

        var drifter = _factory.As("drifter", "drifter@example.com", "Drifter");

        Assert.Equal(HttpStatusCode.Forbidden, (await drifter.GetAsync(ClientAssembly)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await drifter.PostAsJsonAsync("/api/ext/mcp/tokens", new { description = "x" })).StatusCode);

        // The same request from a Solo user still works, so the refusals above
        // are about who is asking.
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync(ClientAssembly)).StatusCode);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT count(*) FROM ext_mcp."Tokens" """, connection);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
