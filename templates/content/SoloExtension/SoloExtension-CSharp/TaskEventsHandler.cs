using Microsoft.Extensions.Logging;
using Solo.Extensibility;

namespace SoloExtension_CSharp;

/// <summary>
/// Runs after a change to a task is saved.
/// </summary>
/// <remarks>
/// It can't refuse the change, and a throw here doesn't undo it. Delivery is
/// in-process and best-effort: there is no outbox, so don't build anything on
/// this that must never miss an event.
/// </remarks>
public sealed class TaskEventsHandler(ILogger<TaskEventsHandler> logger) : ISoloEventHandler
{
    public IReadOnlySet<string> EntityTypes { get; } = new HashSet<string> { Solo.Shared.Hubs.EntityTypes.Task };

    public ValueTask HandleAsync(SoloDomainEvent domainEvent, CancellationToken ct)
    {
        logger.LogInformation(
            "Task {TaskId} {Kind}: {ChangedFields}",
            domainEvent.EntityId, domainEvent.Kind, string.Join(", ", domainEvent.ChangedFields));

        return ValueTask.CompletedTask;
    }
}
