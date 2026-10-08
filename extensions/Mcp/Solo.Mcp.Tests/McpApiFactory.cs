using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Solo.Mcp.Tests;

/// <summary>
/// The host with the MCP package loaded — from a folder of zips when given one,
/// as a deployment loads it, otherwise from the directory this project's build
/// staged — plus the one piece of wiring a deployment gets from its socket.
/// </summary>
public sealed class McpApiFactory(string connectionString, string? extensionsDirectory = null)
    : SoloApiFactory(
        connectionString,
        extensionsDirectory is null ? [PackageDirectory("mcp")] : null,
        extensionsDirectory)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // The MCP tools re-enter the API over HTTP (see SoloRestClient). There
        // is no socket under WebApplicationFactory, so the loopback hop is
        // pointed at the TestServer's own in-memory handler below — which is
        // the same pipeline, minus the wire.
        builder.UseSetting("Mcp:ApiBaseUrl", "http://localhost/");

        // Resolved lazily: the host does not exist yet here, but it does by the
        // time an MCP tool first asks for a client.
        builder.ConfigureServices(services => services.AddHttpClient("solo-mcp")
            .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler()));
    }
}
