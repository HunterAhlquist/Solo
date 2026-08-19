using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// Boots the instance from a zipped MCP package dropped into the extensions
/// directory — the way an operator installs it, and the way
/// <c>build/pack-mcp-extension.sh</c> ships it.
/// </summary>
/// <remarks>
/// <c>McpExtensionTests</c> loads the same package unzipped, which is the faster
/// loop; this one exists because the archive is what users receive, and a
/// package that only works unzipped would pass every other test here.
/// </remarks>
[Collection("postgres")]
public sealed class McpPackageTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string _extensionsDirectory = null!;
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _extensionsDirectory = Path.Combine(Path.GetTempPath(), $"solo-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_extensionsDirectory);
        ZipFile.CreateFromDirectory(
            SoloApiFactory.PackageDirectory("mcp"),
            Path.Combine(_extensionsDirectory, "solo-mcp.zip"));

        var connectionString = await postgres.CreateDatabaseAsync("mcp_zip");
        _factory = new SoloApiFactory(connectionString, extensionsDirectory: _extensionsDirectory);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_extensionsDirectory is not null && Directory.Exists(_extensionsDirectory))
        {
            Directory.Delete(_extensionsDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task The_shipped_archive_installs_and_serves_mcp()
    {
        postgres.SkipUnlessDockerAvailable();

        // It loaded, with its schema, and nothing was reported as a failure.
        var extensions = await _admin.GetJson<ExtensionAdminDto>("/api/extensions/admin");
        Assert.Empty(extensions.Failures);
        var registration = Assert.Single(extensions.Extensions, e => e.ExtensionId == "mcp");
        Assert.Equal("Loaded", registration.State);
        Assert.Equal("ext_mcp", registration.SchemaName);

        // The manifest's version is the package's identity: the host records it,
        // and the downgrade guard compares against it on the next boot.
        Assert.False(string.IsNullOrWhiteSpace(registration.Version));

        // And it works: mint a token, speak MCP with it.
        var minted = await _admin.PostJson<JsonObject>("/api/ext/mcp/tokens", new { });
        var value = minted["value"]!.GetValue<string>();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value);
        var response = await client.PostAsync(
            "/api/ext/mcp",
            new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                System.Text.Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.NotEmpty(body["result"]!["tools"]!.AsArray());
    }

    /// <summary>
    /// The package's declared version and the assembly it ships have to agree —
    /// the pack script enforces it, and this is what would catch a hand-assembled
    /// archive that skipped the script.
    /// </summary>
    [SkippableFact]
    public async Task The_manifest_version_matches_the_assembly_it_ships()
    {
        postgres.SkipUnlessDockerAvailable();

        var extensions = await _admin.GetJson<ExtensionAdminDto>("/api/extensions/admin");
        var manifestVersion = Assert.Single(extensions.Extensions, e => e.ExtensionId == "mcp").Version;

        var minted = await _admin.PostJson<JsonObject>("/api/ext/mcp/tokens", new { });
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", minted["value"]!.GetValue<string>());

        var info = await client.GetJson<JsonObject>("/api/ext/mcp/info");
        Assert.StartsWith(manifestVersion!, info["serverVersion"]!.GetValue<string>(), StringComparison.Ordinal);
    }
}
