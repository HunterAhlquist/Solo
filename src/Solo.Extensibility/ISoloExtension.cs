using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Solo.Extensibility;

/// <summary>
/// The single entry point the host looks for in a package's server assembly.
/// Exactly one implementation per assembly.
/// </summary>
/// <remarks>
/// Called before <c>builder.Build()</c>, because MVC application parts and
/// filters cannot be added afterwards. An implementation that throws is
/// reported as a load failure and the extension is disabled; the instance
/// still starts.
/// </remarks>
public interface ISoloExtension
{
    void ConfigureServices(IServiceCollection services, IExtensionContext context);
}

/// <summary>What the host tells an extension about itself at load time.</summary>
public interface IExtensionContext
{
    ExtensionManifest Manifest { get; }

    /// <summary>Manifest id — the route segment, schema name and log prefix.</summary>
    string Id { get; }

    /// <summary>
    /// Postgres schema this extension owns, or null when the manifest declared
    /// none. <see cref="ExtensionServiceCollectionExtensions.AddExtensionDbContext{TContext}"/>
    /// requires it.
    /// </summary>
    string? Schema { get; }

    /// <summary>
    /// The core database connection string. Note this reaches the whole
    /// database, not just the extension's schema — see the trust model in
    /// docs/extension-plan.md §1. A schema is a namespace, not a boundary.
    /// </summary>
    string ConnectionString { get; }

    ILoggerFactory LoggerFactory { get; }
}
