using Solo.Shared;

namespace Solo.Data.Entities;

/// <summary>Entities carrying the xmin optimistic-concurrency token.</summary>
public interface IVersioned
{
    uint Version { get; set; }
}

/// <summary>Provisioned on first SSO sign-in; identity is the OIDC iss + sub pair.</summary>
public class User : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public bool IsSuperAdmin { get; set; }
    public uint Version { get; set; }
}

public class Project : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public string? Icon { get; set; }
    /// <summary>An uploaded image used in place of the curated <see cref="Icon"/> when set.</summary>
    public Guid? ImageId { get; set; }
    public uint Version { get; set; }

    public List<Board> Boards { get; set; } = [];
    public List<ProjectMember> Members { get; set; } = [];
    public List<Label> Labels { get; set; } = [];
}

/// <summary>
/// The whole access model. <see cref="UserId"/> is null while the invite is
/// pending; it activates when that email first signs in.
/// </summary>
public class ProjectMember : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>Invited email, normalized to lower case.</summary>
    public required string Email { get; set; }
    public ProjectRole Role { get; set; }
    public bool IsOwner { get; set; }
    public uint Version { get; set; }

    public Project? Project { get; set; }
    public User? User { get; set; }
}

public class Board : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public string? Icon { get; set; }
    /// <summary>An uploaded image used in place of the curated <see cref="Icon"/> when set.</summary>
    public Guid? ImageId { get; set; }
    public bool IsOpen { get; set; } = true;
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
    public required string Name { get; set; }
    public Guid? LabelId { get; set; }
    /// <summary>Markdown.</summary>
    public string? Description { get; set; }
    /// <summary>
    /// Drives column placement. With sub-tasks present the value is auto-derived
    /// (see <c>TaskStateCalculator</c>) and manual writes are rejected by the API.
    /// </summary>
    public TaskState Status { get; set; }
    public decimal Rank { get; set; }
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

/// <summary>Null <see cref="BoardId"/> = project-global; non-null = board-local addition.</summary>
public class Label : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public Guid? BoardId { get; set; }
    public LabelKind Kind { get; set; }
    public required string Name { get; set; }
    public required string Color { get; set; }
    /// <summary>Curated icon name (see <c>SoloIcons</c>), or null.</summary>
    public string? Icon { get; set; }
    /// <summary>An uploaded image used in place of the curated <see cref="Icon"/> when set.</summary>
    public Guid? ImageId { get; set; }
    public uint Version { get; set; }

    public Project? Project { get; set; }
    public Board? Board { get; set; }
}

/// <summary>
/// An uploaded raster or vector image, stored inline in Postgres. Used as the
/// custom icon for a project, board or label. Immutable once created — the id
/// is the cache key, so content never changes under a given id.
/// </summary>
public class ImageAsset
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One table; exactly one parent FK set (check constraint).</summary>
public class Comment : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AuthorId { get; set; }
    /// <summary>Markdown.</summary>
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? EpicId { get; set; }
    public Guid? TaskId { get; set; }
    public uint Version { get; set; }

    public User? Author { get; set; }
    public Epic? Epic { get; set; }
    public TaskItem? Task { get; set; }
}

/// <summary>Many-to-many user assignment; exactly one parent FK set (check constraint).</summary>
public class Assignment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid? EpicId { get; set; }
    public Guid? TaskId { get; set; }

    public User? User { get; set; }
    public Epic? Epic { get; set; }
    public TaskItem? Task { get; set; }
}

/// <summary>
/// One row per changed column, written by the SaveChanges interceptor in the
/// same transaction as the change. Stores only the previous value: history,
/// not state. <see cref="ProjectId"/> is denormalized so per-project audit
/// queries survive entity deletion.
/// </summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ProjectId { get; set; }
    public required string TableName { get; set; }
    public required string RowId { get; set; }
    /// <summary>Null on row creation events.</summary>
    public string? ColumnName { get; set; }
    public string? PreviousValue { get; set; }
}

/// <summary>Single-row config table (Id fixed at 1).</summary>
public class InstanceSettings : IVersioned
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public int AuditRetentionDays { get; set; } = 90;
    /// <summary>Once true, the static setup account is permanently disabled.</summary>
    public bool SetupCompleted { get; set; }
    public uint Version { get; set; }
}
