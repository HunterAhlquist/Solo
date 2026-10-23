using Microsoft.Extensions.DependencyInjection;
using Solo.DesignDocs.Data;
using Solo.DesignDocs.Live;
using Solo.Extensibility;

namespace Solo.DesignDocs;

/// <summary>The package's entry point: the one type the host looks for.</summary>
/// <remarks>
/// Everything lives in this package's own schema. Core is read through
/// <see cref="ISoloQuery"/> for membership and tasks, and never written. Live
/// editing rides on core's hub through <see cref="DocumentSyncHandler"/>, so
/// the package opens no socket of its own.
/// </remarks>
public sealed class DesignDocsExtension : ISoloExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
        // The context pins its migrations history into the manifest's schema, so
        // the two disagreeing would put tables and history in different places.
        if (!string.Equals(context.Schema, DesignDocsDbContext.Schema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"extension.json declares schema \"{context.Schema}\" but {nameof(DesignDocsDbContext)} " +
                $"is built for \"{DesignDocsDbContext.Schema}\".");
        }

        services.AddExtensionDbContext<DesignDocsDbContext>(context);
        services.AddScoped<DocumentLog>();

        // Process-wide state for the live handler, which is created per message.
        services.AddSingleton<DocumentLocks>();
        services.AddSingleton<SeedLeases>();
        services.AddSingleton<PartAssembler>();
        services.AddSingleton<DocumentDirectory>();
        services.AddLiveMessageHandler<DocumentSyncHandler>();
    }
}
