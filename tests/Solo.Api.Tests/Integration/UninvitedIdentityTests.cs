using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// A valid provider sign-in is not a Solo user. Anyone with a Google account
/// can get an ID token for the instance's client ID from its own sign-in page,
/// and these endpoints used to serve such a caller because the policies asked
/// only for authentication: image uploads from anyone on the internet, the
/// extension inventory and assemblies, and MCP tokens that never expire.
/// </summary>
[Collection("postgres")]
public sealed class UninvitedIdentityTests(PostgresFixture postgres) : IAsyncLifetime
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

        _connectionString = await postgres.CreateDatabaseAsync("uninvited");
        _factory = new SoloApiFactory(_connectionString, TestExtensions.Mcp);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task An_identity_nobody_invited_is_refused_and_leaves_nothing_behind()
    {
        postgres.SkipUnlessDockerAvailable();

        var drifter = _factory.As("drifter", "drifter@example.com", "Drifter");

        Assert.Equal(HttpStatusCode.Forbidden, (await drifter.PostAsync("/api/images", Png())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await drifter.GetAsync("/api/extensions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await drifter.GetAsync("/api/extensions/mcp/client/Solo.Mcp.Client.dll")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await drifter.PostAsJsonAsync("/api/ext/mcp/tokens", new { description = "x" })).StatusCode);

        // The same requests from a Solo user still work, so the refusals above
        // are about who is asking.
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsync("/api/images", Png())).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.GetAsync("/api/extensions/mcp/client/Solo.Mcp.Client.dll")).StatusCode);

        // Public endpoints stay public with a token attached.
        Assert.Equal(HttpStatusCode.OK, (await drifter.GetAsync("/api/config/auth")).StatusCode);

        Assert.Equal(1, await CountAsync("""SELECT count(*) FROM "ImageAssets" """));
        Assert.Equal(0, await CountAsync("""SELECT count(*) FROM ext_mcp."Tokens" """));
    }

    [SkippableFact]
    public async Task Only_someone_who_could_place_an_icon_may_upload_one()
    {
        postgres.SkipUnlessDockerAvailable();

        var project = await _admin.PostJson<ProjectDto>("/api/projects", new { name = "Icons", alias = "ICONS" });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "reader@example.com", role = "Read" });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = "writer@example.com", role = "ReadWrite" });

        var reader = _factory.As("reader", "reader@example.com");
        var writer = _factory.As("writer", "writer@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/images", Png())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await writer.PostAsync("/api/images", Png())).StatusCode);
    }

    private static MultipartFormDataContent Png()
    {
        var file = new ByteArrayContent([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { file, "file", "icon.png" } };
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
