using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>The host broadcasts committed field changes to connected clients.</summary>
public interface IExtensionFieldNotifier
{
    Task ChangedAsync(ExtensionEntity entity, Guid entityId);
}
