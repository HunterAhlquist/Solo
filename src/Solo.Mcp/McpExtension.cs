using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;
using Solo.Mcp.Auth;
using Solo.Mcp.Data;
using Solo.Mcp.Protocol;
using Solo.Mcp.Rest;

namespace Solo.Mcp;

/// <summary>
/// The package's entry point — the one type the host looks for.
/// </summary>
/// <remarks>
/// Everything this feature needs is registered here: its own schema and
/// migrations, its own credential and the authentication that accepts it, and
/// the endpoints that serve MCP and manage tokens. Nothing about it exists in
/// core, so removing the package removes the feature and its attack surface
/// with it — the schema is then reported as data without a package and can be
/// purged from Admin settings.
/// </remarks>
public sealed class McpExtension : ISoloExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
        // The context pins its migrations history into the manifest's schema, so
        // the two disagreeing would put tables and history in different places.
        if (!string.Equals(context.Schema, McpDbContext.Schema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"extension.json declares schema \"{context.Schema}\" but {nameof(McpDbContext)} " +
                $"is built for \"{McpDbContext.Schema}\".");
        }

        services.AddExtensionDbContext<McpDbContext>(context);

        // The credential. A scheme so the handler is a normal one, plus a
        // startup filter that runs it at the front of the pipeline — core
        // resolves the user from HttpContext.User before authorization, so a
        // principal produced any later would be invisible to every policy.
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, McpTokenAuthenticationHandler>(
                McpAuthentication.Scheme, null);
        services.AddSingleton<IStartupFilter, McpAuthenticationStartupFilter>();

        services.AddHttpClient(SoloRestClient.HttpClientName, client =>
        {
            // Long enough for a board read on a cold connection, short enough
            // that a wedged call fails the tool instead of the conversation.
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Solo-MCP/1.0");
        });

        services.AddScoped<SoloRestClient>();
        services.AddScoped<McpToolCatalog>();
        services.AddScoped<McpServer>();
    }
}
