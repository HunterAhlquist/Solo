using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;

namespace Hello;

/// <summary>
/// The entry point — the one type the host looks for in the server assembly.
/// This extension has nothing to register: MVC finds the controller on its own,
/// and <see cref="ISoloQuery"/> is the host's.
/// </summary>
public sealed class HelloExtension : ISoloExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
    }
}
