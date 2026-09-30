using Solo.Shared.Dtos;

namespace Solo.Shared.Extensibility;

/// <summary>
/// The host's task drawer, opened over whatever page is showing — the same
/// drawer a board uses, with its presence, editing warning and save rules.
/// </summary>
/// <remarks>
/// An extension page can't render <c>DetailDrawer</c> itself: it lives in
/// Solo.Client and leans on the board state there. So the host opens it on the
/// extension's behalf. Inject this into a client page; a client half can't
/// register services of its own, because its assembly loads after the container
/// is built.
/// </remarks>
public interface ISoloTaskDrawer
{
    /// <summary>Opens the host's task drawer over the current page.</summary>
    Task OpenAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>After the drawer saves (carries the fresh TaskDto) or deletes/moves the task.</summary>
    event Action<TaskDrawerChange>? Changed;

    event Action? Closed;
}

/// <summary>What happened to the task open in the host drawer.</summary>
/// <param name="Task">
/// The task as it now stands: the saved DTO, or the moved one with its new board
/// and number. Null when the task was deleted or can no longer be read.
/// </param>
public sealed record TaskDrawerChange(TaskDrawerChangeKind Kind, Guid TaskId, TaskDto? Task);

public enum TaskDrawerChangeKind
{
    /// <summary>A field, the checklist or the assignees changed — here or remotely.</summary>
    Saved = 0,

    /// <summary>Moved to another board. The drawer then follows it there, as it does on a board.</summary>
    Moved = 1,

    /// <summary>Deleted, or no longer readable by this user.</summary>
    Deleted = 2,
}
