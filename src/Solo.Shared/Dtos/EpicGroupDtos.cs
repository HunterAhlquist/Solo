using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

public sealed record EpicGroupDto(Guid Id, Guid BoardId, string Name, decimal Rank, uint Version);

public sealed class CreateEpicGroupRequest
{
    [Required, MaxLength(FieldLimits.ItemName)]
    public required string Name { get; init; }
}

public sealed class UpdateEpicGroupRequest
{
    [Required, MaxLength(FieldLimits.ItemName)]
    public required string Name { get; init; }
    public uint Version { get; init; }
}

public sealed class MoveEpicRequest
{
    public Guid? GroupId { get; init; }
    public Guid? PrecedingId { get; init; }
    public Guid? FollowingId { get; init; }
    public uint Version { get; init; }
}

public sealed class RerankEpicGroupRequest
{
    public Guid? PrecedingId { get; init; }
    public Guid? FollowingId { get; init; }
    public uint Version { get; init; }
}
