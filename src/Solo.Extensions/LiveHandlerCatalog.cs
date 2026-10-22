using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>A client may address only the handler owned by the topic's extension prefix.</summary>
public sealed class LiveHandlerCatalog
{
    public (string ExtensionId, ISoloLiveMessageHandler Handler)? Find(
        string topic, IServiceProvider services, ExtensionState state)
    {
        var colon = topic.IndexOf(':');
        if (colon <= 0) return null;
        var id = topic[..colon];
        var package = state.Find(id);
        if (package?.LoadedAssembly is null || state.IsDisabled(id, out _)) return null;

        var registration = services.GetServices<LiveMessageHandlerRegistration>()
            .SingleOrDefault(r => ReferenceEquals(r.HandlerType.Assembly, package.LoadedAssembly));
        return registration is null ? null
            : (id, (ISoloLiveMessageHandler)services.GetRequiredService(registration.HandlerType));
    }
}
