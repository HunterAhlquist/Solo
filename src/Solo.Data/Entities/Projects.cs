using Solo.Shared;

namespace Solo.Data.Entities;

public class Project : IVersioned
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    /// <summary>Short serial-number prefix (A-Z0-9, max 8), unique across projects.</summary>
    public required string Alias { get; set; }
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
