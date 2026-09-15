using Solo.Shared;

namespace Solo.Data.Entities;

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
