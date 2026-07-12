using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

public sealed record BoardDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Alias,
    string? Icon,
    Guid? ImageId,
    bool IsOpen,
    uint Version);

/// <summary>
/// Full board payload: everything needed to render the Kanban view.
/// <paramref name="ProjectAlias"/> rides along so the client can compose task
/// serial numbers (PROJECT-BOARD-N) without a second fetch.
/// </summary>
public sealed record BoardDetailDto(
    Guid Id,
    Guid ProjectId,
    string ProjectAlias,
    string Name,
    string Alias,
    string? Icon,
    Guid? ImageId,
    bool IsOpen,
    uint Version,
    IReadOnlyList<EpicDto> Epics,
    IReadOnlyList<TaskDto> Tasks,
    IReadOnlyList<LabelDto> Labels);

public sealed class CreateBoardRequest
{
    [Required, MaxLength(FieldLimits.EntityName)]
    public required string Name { get; init; }

    [Required, RegularExpression(Aliases.Pattern, ErrorMessage = Aliases.PatternError)]
    public required string Alias { get; init; }

    [MaxLength(FieldLimits.IconName)]
    public string? Icon { get; init; }

    public Guid? ImageId { get; init; }
}

public sealed class UpdateBoardRequest
{
    [Required, MaxLength(FieldLimits.EntityName)]
    public required string Name { get; init; }

    [Required, RegularExpression(Aliases.Pattern, ErrorMessage = Aliases.PatternError)]
    public required string Alias { get; init; }

    [MaxLength(FieldLimits.IconName)]
    public string? Icon { get; init; }

    public Guid? ImageId { get; init; }

    public uint Version { get; init; }
}

public sealed class SetBoardOpenRequest
{
    public bool IsOpen { get; init; }
}

/// <summary>
/// Re-rank request: place the entity between its two new siblings.
/// Null preceding = move to the head; null following = move to the tail.
/// The server computes the fractional rank (and rebalances when midpoints run out).
/// </summary>
public sealed class RerankRequest
{
    public Guid? PrecedingId { get; init; }

    public Guid? FollowingId { get; init; }
}
