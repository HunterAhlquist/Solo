using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;

namespace Solo.Extension.Fixture;

/// <summary>The package's entry point — the one type the host looks for.</summary>
public sealed class FixtureExtension : ISoloExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
        services.AddExtensionDbContext<FixtureDbContext>(context);
        services.AddOperationHook<FixtureHook>();
        services.AddEventHandler<FixtureEventHandler>();
        services.AddSingleton<FixtureEventLog>();
    }
}
