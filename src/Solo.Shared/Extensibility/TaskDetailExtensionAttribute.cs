namespace Solo.Shared.Extensibility;

/// <summary>
/// Marks a component rendered below task assignments. Components receive Task
/// (TaskDto), CanWrite (bool), and OnChanged (EventCallback) parameters. Invoke
/// OnChanged after saving so the host refreshes its task and concurrency version.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TaskDetailExtensionAttribute : Attribute;
