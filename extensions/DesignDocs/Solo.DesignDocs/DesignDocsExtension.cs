using Microsoft.Extensions.DependencyInjection;
using Solo.DesignDocs.Data;
using Solo.Extensibility;

namespace Solo.DesignDocs;

public sealed class DesignDocsExtension : ISoloExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
        if (!string.Equals(context.Schema, DesignDocsDbContext.Schema, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected schema {DesignDocsDbContext.Schema}.");
        services.AddExtensionDbContext<DesignDocsDbContext>(context);
    }
}
