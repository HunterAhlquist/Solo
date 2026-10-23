using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>
/// Finds the extension that owns a live topic, and its message handler.
/// </summary>
/// <remarks>
/// <para>A topic belongs to the extension whose id comes before its first
/// <c>:</c>. Ownership is read from the handler's assembly, as
/// <see cref="HookCatalog"/> does for hooks, so a package can't register a
/// handler that answers for another extension's prefix.</para>
///
/// <para>The handler is resolved from the caller's scope per message, and only
/// that one: constructing every extension's handler to ask which one wants the
/// message would put every handler's dependencies on each keystroke's path.</para>
/// </remarks>
public sealed class LiveHandlerCatalog(IEnumerable<LiveMessageHandlerRegistration> registrations, ExtensionState state)
{
    private readonly IReadOnlyList<LiveMessageHandlerRegistration> _registrations = registrations.ToList();
    private readonly ConcurrentDictionary<string, Type?> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The id a topic names as its owner, or null when it names none.</summary>
    public static string? OwnerOf(string topic)
    {
        var colon = topic.IndexOf(':');
        return colon > 0 ? topic[..colon] : null;
    }

    /// <summary>
    /// The handler for a topic, with its extension's id, or null when the owner
    /// registered none or is disabled.
    /// </summary>
    public (string ExtensionId, ISoloLiveMessageHandler Handler)? Find(IServiceProvider services, string topic)
    {
        if (OwnerOf(topic) is not { } extensionId || state.IsDisabled(extensionId, out _))
        {
            return null;
        }

        var type = _byExtension.GetOrAdd(extensionId, id =>
        {
            var package = state.Find(id);
            return package?.LoadedAssembly is { } assembly
                ? _registrations.FirstOrDefault(r => r.HandlerType.Assembly == assembly)?.HandlerType
                : null;
        });

        return type is null
            ? null
            : (state.Find(extensionId)!.Id, (ISoloLiveMessageHandler)services.GetRequiredService(type));
    }
}
