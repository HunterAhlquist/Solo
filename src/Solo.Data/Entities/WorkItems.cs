using Solo.Shared;

namespace Solo.Data.Entities;

public class Board : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    /// <summary>Short serial-number segment (A-Z0-9, max 8), unique within the project.</summary>
    public required string Alias { get; set; }
    /// <summary>
    /// The serial number the board's next task will take. A counter rather than
    /// max+1 so a deleted task's number is never reassigned to a different task.
    /// </summary>
    public int NextTaskNumber { get; set; } = 1;
    public string? Icon { get; set; }
    /// <summary>An uploaded image used in place of the curated <see cref="Icon"/> when set.</summary>
    public Guid? ImageId { get; set; }
    public bool IsOpen { get; set; } = true;
    /// <summary>
    /// Per-extension custom fields, keyed by extension id (see
    /// <c>IExtensionFieldStore</c>). Lives here rather than in the extension's
    /// own schema so an extension can annotate a core row without a schema
    /// change — which is also why a purge has to strip these keys as well as
    /// drop the schema.
    /// </summary>
    public string? Extensions { get; set; }
    public uint Version { get; set; }

    public Project? Project { get; set; }
    public List<Epic> Epics { get; set; } = [];
    public List<TaskItem> Tasks { get; set; } = [];
}

/// <summary>Renders as a visual container surrounding its child tasks on the board.</summary>
public class Epic : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public required string Name { get; set; }
    /// <summary>Markdown.</summary>
    public string? Description { get; set; }
    public decimal Rank { get; set; }
    /// <summary>
    /// Per-extension custom fields, keyed by extension id (see
    /// <c>IExtensionFieldStore</c>). Lives here rather than in the extension's
    /// own schema so an extension can annotate a core row without a schema
    /// change — which is also why a purge has to strip these keys as well as
    /// drop the schema.
    /// </summary>
    public string? Extensions { get; set; }
    public uint Version { get; set; }

    public Board? Board { get; set; }
    public List<TaskItem> Tasks { get; set; } = [];
    public List<Comment> Comments { get; set; } = [];
    public List<Assignment> Assignments { get; set; } = [];
}

/// <summary>
/// The plan's <c>Task</c> entity — named <c>TaskItem</c> in code to avoid the
/// <c>System.Threading.Tasks.Task</c> collision; the table is still "Tasks".
/// Null <see cref="EpicId"/> = ad-hoc task sitting directly on the board.
/// </summary>
public class TaskItem : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid BoardId { get; set; }
    public Guid? EpicId { get; set; }
    /// <summary>
    /// Per-board auto-increment, assigned on create and never reused. The
    /// user-facing serial number PROJECT-BOARD-N is composed at runtime from the
    /// two aliases and this value.
    /// </summary>
    public int Number { get; set; }
    public required string Name { get; set; }
    public Guid? LabelId { get; set; }
    /// <summary>Markdown.</summary>
    public string? Description { get; set; }
    /// <summary>
    /// Drives column placement. With sub-tasks present the value is auto-derived
    /// (see <c>TaskStateCalculator</c>) and manual writes are rejected by the API.
    /// </summary>
    public TaskState Status { get; set; }
    /// <summary>Anchors the deadline countdown: progress runs from here to <see cref="DeadlineAt"/>.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Optional due date. A date, not an instant — "due July 30" regardless of timezone.</summary>
    public DateOnly? DeadlineAt { get; set; }
    public decimal Rank { get; set; }
    /// <summary>
    /// Per-extension custom fields, keyed by extension id (see
    /// <c>IExtensionFieldStore</c>). Lives here rather than in the extension's
    /// own schema so an extension can annotate a core row without a schema
    /// change — which is also why a purge has to strip these keys as well as
    /// drop the schema.
    /// </summary>
    public string? Extensions { get; set; }
    public uint Version { get; set; }

    public Board? Board { get; set; }
    public Epic? Epic { get; set; }
    public Label? Label { get; set; }
    public List<SubTask> SubTasks { get; set; } = [];
    public List<Comment> Comments { get; set; } = [];
    public List<Assignment> Assignments { get; set; } = [];
}

/// <summary>Checklist item.</summary>
public class SubTask : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TaskId { get; set; }
    public required string Name { get; set; }
    public Guid? LabelId { get; set; }
    public bool IsFinished { get; set; }
    public decimal Rank { get; set; }
    public uint Version { get; set; }

    public TaskItem? Task { get; set; }
    public Label? Label { get; set; }
}
