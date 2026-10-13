using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;
#if (Database)
using SoloExtension_CSharp.Data;
#endif

namespace SoloExtension_CSharp;

/// <summary>
/// The entry point: the one type the host looks for in the server assembly. It
/// runs once, before the host builds its services, so whatever is registered
/// here is there for the life of the instance.
/// </summary>
public sealed class StarterExtension : ISoloExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
        // Controllers need no registration. MVC finds them, and the host puts
        // every route under /api/ext/solo-extension-id/.
#if (Database)

        // The context keeps its migrations history in the manifest's schema, so
        // the two disagreeing would put the tables and their history in
        // different places. Throwing here disables the extension with this
        // message; the instance still starts.
        if (!string.Equals(context.Schema, StarterDbContext.Schema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"extension.json declares schema \"{context.Schema}\" but {nameof(StarterDbContext)} " +
                $"is built for \"{StarterDbContext.Schema}\".");
        }

        services.AddExtensionDbContext<StarterDbContext>(context);
#endif
#if (OperationHook)
        services.AddOperationHook<TaskStatusHook>();
#endif
#if (EventHandler)
        services.AddEventHandler<TaskEventsHandler>();
#endif
    }
}
