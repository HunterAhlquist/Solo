using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

public sealed record EpicDto(
    Guid Id,
    Guid BoardId,
    string Name,
    string? Description,
    decimal Rank,
    uint Version,
    IReadOnlyList<Guid> AssigneeIds);

public sealed record TaskDto(
    Guid Id,
    Guid BoardId,
    Guid? EpicId,
    int Number,
    string Name,
    Guid? LabelId,
    string? Description,
    TaskState Status,
    decimal Rank,
    uint Version,
    IReadOnlyList<Guid> AssigneeIds,
    IReadOnlyList<SubTaskDto> SubTasks);

public sealed record SubTaskDto(
    Guid Id,
    Guid TaskId,
    string Name,
    Guid? LabelId,
    bool IsFinished,
    decimal Rank,
    uint Version);

public sealed class CreateEpicRequest
{
    [Required, MaxLength(FieldLimits.ItemName)]
    public required string Name { get; init; }

    [MaxLength(FieldLimits.Description)]
    public string? Description { get; init; }
}

public sealed class UpdateEpicRequest
{
    [Required, MaxLength(FieldLimits.ItemName)]
    public required string Name { get; init; }

    [MaxLength(FieldLimits.Description)]
    public string? Description { get; init; }

    public uint Version { get; init; }
}

public sealed class CreateTaskRequest
{
    [Required, MaxLength(FieldLimits.ItemName)]
    public required string Name { get; init; }

    /// <summary>Null = ad-hoc task sitting directly on the board.</summary>
    public Guid? EpicId { get; init; }

    public Guid? LabelId { get; init; }

    [MaxLength(FieldLimits.Description)]
    public string? Description { get; init; }
}

public sealed class UpdateTaskRequest
{
    [Required, MaxLength(FieldLimits.ItemName)]
    public required string Name { get; init; }

    public Guid? LabelId { get; init; }

    [MaxLength(FieldLimits.Description)]
    public string? Description { get; init; }

    public uint Version { get; init; }
}

public sealed class SetTaskStatusRequest
{
    public TaskState Status { get; init; }
}

public sealed class SetTaskEpicRequest
{
    /// <summary>Null detaches the task to ad-hoc.</summary>
    public Guid? EpicId { get; init; }
}

public sealed class SetAssigneesRequest
{
    public required IReadOnlyList<Guid> UserIds { get; init; }
}

public sealed class CreateSubTaskRequest
{
    [Required, MaxLength(FieldLimits.ItemName)]
    public required string Name { get; init; }

    public Guid? LabelId { get; init; }
}

public sealed class UpdateSubTaskRequest
{
    [Required, MaxLength(FieldLimits.ItemName)]
    public required string Name { get; init; }

    public Guid? LabelId { get; init; }

    public uint Version { get; init; }
}

public sealed class SetSubTaskFinishedRequest
{
    public bool IsFinished { get; init; }
}
