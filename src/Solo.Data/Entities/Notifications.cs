using Solo.Shared;

namespace Solo.Data.Entities;

/// <summary>
/// One entry in a person's inbox. <see cref="Title"/> and <see cref="Body"/> are
/// written when it is raised, so it still reads after its task is deleted; the
/// work-item ids are nulled then rather than taking the entry with them.
/// </summary>
public class Notification : INotAudited
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public NotificationKind Kind { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? BoardId { get; set; }
    public Guid? TaskId { get; set; }
    public Guid? EpicId { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }
    /// <summary>
    /// Unique per user: what makes raising the same thing twice a no-op, such as
    /// a deadline reminder on every sweep. A deadline's key carries the date, so
    /// moving the deadline earns fresh reminders.
    /// </summary>
    public required string DedupeKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }

    public User? User { get; set; }
}

/// <summary>One row per user, made on first read with everything switched on.</summary>
public class NotificationPreferences : IVersioned, INotAudited
{
    public Guid UserId { get; set; }
    /// <summary>IANA id, as the browser reports it. Null means UTC.</summary>
    public string? TimeZone { get; set; }
    public bool NotifyOnAssigned { get; set; } = true;
    public bool NotifyOnDeadline { get; set; } = true;
    public uint Version { get; set; }

    public User? User { get; set; }
}
