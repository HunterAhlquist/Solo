using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

public sealed record LabelDto(
    Guid Id,
    Guid ProjectId,
    Guid? BoardId,
    LabelKind Kind,
    string Name,
    string Color,
    string? Icon,
    Guid? ImageId,
    uint Version);

public sealed class CreateLabelRequest
{
    /// <summary>Null = project-global; non-null = board-local addition.</summary>
    public Guid? BoardId { get; init; }

    public LabelKind Kind { get; init; }

    [Required, MaxLength(100)]
    public required string Name { get; init; }

    [Required, RegularExpression("^#[0-9a-fA-F]{6}$")]
    public required string Color { get; init; }

    [MaxLength(100)]
    public string? Icon { get; init; }

    public Guid? ImageId { get; init; }
}

public sealed class UpdateLabelRequest
{
    [Required, MaxLength(100)]
    public required string Name { get; init; }

    [Required, RegularExpression("^#[0-9a-fA-F]{6}$")]
    public required string Color { get; init; }

    [MaxLength(100)]
    public string? Icon { get; init; }

    public Guid? ImageId { get; init; }

    public uint Version { get; init; }
}

public sealed record CommentDto(
    Guid Id,
    Guid AuthorId,
    string? AuthorDisplayName,
    string? AuthorAvatarUrl,
    string Body,
    DateTime CreatedAt,
    Guid? EpicId,
    Guid? TaskId,
    uint Version);

public sealed class CreateCommentRequest
{
    [Required, MaxLength(100_000)]
    public required string Body { get; init; }
}

public sealed class UpdateCommentRequest
{
    [Required, MaxLength(100_000)]
    public required string Body { get; init; }

    public uint Version { get; init; }
}
